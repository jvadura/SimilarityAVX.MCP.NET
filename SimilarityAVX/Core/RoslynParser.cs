using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpMcpServer.Models;

namespace CSharpMcpServer.Core;

public class RoslynParser
{
    private readonly int MaxChunkSize; // 100KB, do NOT use for chunking, only truncating for embedding model!
    private readonly bool _includeFilePath;
    private readonly bool _includeProjectContext;
    private readonly SlidingWindowConfig _slidingWindowConfig; // Use for smart chunking
    
    public RoslynParser(bool includeFilePath = false, bool includeProjectContext = false, int maxChunkSize = 100000, SlidingWindowConfig? slidingWindowConfig = null)
    {
        _includeFilePath = includeFilePath;
        _includeProjectContext = includeProjectContext;
        MaxChunkSize = maxChunkSize;
        _slidingWindowConfig = slidingWindowConfig ?? new SlidingWindowConfig();
    }
    
    public List<CodeChunk> ParseFile(string filePath)
    {
        try
        {
            // Handle different file types
            if (filePath.EndsWith(".razor") || filePath.EndsWith(".cshtml"))
            {
                return ParseRazorFile(filePath);
            }
            
            var code = File.ReadAllText(filePath);
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();
            var chunks = new List<CodeChunk>();
            
            // Check for top-level statements (Program.cs without class)
            var hasTopLevelStatements = root.DescendantNodes()
                .OfType<GlobalStatementSyntax>()
                .Any();
                
            if (hasTopLevelStatements)
            {
                chunks.AddRange(ExtractTopLevelStatements(root, filePath));
            }
            
            // Extract global usings
            var globalUsings = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                .ToList();
                
            if (globalUsings.Any())
            {
                var globalUsingContent = string.Join("\n", globalUsings.Select(u => u.ToString()));
                chunks.Add(CreateChunk(globalUsingContent, globalUsings.First(), filePath, "global_usings"));
            }
            
            // NOTE: Namespace-only chunks removed - namespace information is already included
            // in class, method, and other code chunks through context injection.
            // This reduces index size and improves search relevance.
            
            // Extract classes with documentation
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var context = GetClassContext(cls);
                chunks.Add(CreateChunk(context, cls, filePath, "class"));
            }
            
            // Extract interfaces
            foreach (var iface in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            {
                var context = GetInterfaceContext(iface);
                chunks.Add(CreateChunk(context, iface, filePath, "interface"));
            }
            
            // Extract records
            foreach (var record in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                var context = GetRecordContext(record);
                chunks.Add(CreateChunk(context, record, filePath, "record"));
            }
            
            // Extract methods with their containing class context
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodChunks = GetMethodChunks(method, filePath);
                chunks.AddRange(methodChunks);
            }
            
            // Extract properties with context (including expression-bodied)
            foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                // Extract properties with accessors OR expression-bodied properties
                if ((prop.AccessorList != null && prop.AccessorList.Accessors.Any()) || 
                    prop.ExpressionBody != null)
                {
                    var propContext = GetPropertyWithContext(prop);
                    chunks.Add(CreateChunk(propContext, prop, filePath, "property"));
                }
            }
            
            // Extract enums
            foreach (var enumDecl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                chunks.Add(CreateChunk(enumDecl.ToFullString(), enumDecl, filePath, "enum"));
            }
            
            // Extract local functions (methods within methods)
            foreach (var localFunction in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            {
                var localFunctionChunk = GetLocalFunctionWithContext(localFunction);
                chunks.Add(CreateChunk(localFunctionChunk, localFunction, filePath, "local_function"));
            }
            
            // Extract switch expressions (pattern matching)
            foreach (var switchExpr in root.DescendantNodes().OfType<SwitchExpressionSyntax>())
            {
                var switchChunk = GetSwitchExpressionWithContext(switchExpr);
                chunks.Add(CreateChunk(switchChunk, switchExpr, filePath, "switch_expression"));
            }
            
            // Check if this is a generated file
            if (IsGeneratedFile(filePath, code))
            {
                // For generated files, create a single summary chunk
                chunks.Add(new CodeChunk(
                    $"{filePath}:1",
                    $"// Source Generated File: {Path.GetFileName(filePath)}\n// This file was auto-generated and contains {code.Length} characters",
                    filePath,
                    1,
                    code.Split('\n').Length,
                    "generated"
                ));
            }
            // If no structural elements found, use intelligent sliding window chunking
            else if (!chunks.Any())
            {
                chunks.AddRange(ChunkFileWithSlidingWindow(code, filePath));
            }
            
            // Deduplicate and validate chunk sizes
            return ValidateAndDeduplicateChunks(chunks);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoslynParser] Error parsing {filePath}: {ex.Message}");
            
            // Return whole file as fallback
            try
            {
                var content = File.ReadAllText(filePath);
                return new List<CodeChunk>
                {
                    new CodeChunk(
                        $"{filePath}:1",
                        content.Length > MaxChunkSize ? content.Substring(0, MaxChunkSize) + "\n// ... truncated" : content,
                        filePath,
                        1,
                        content.Split('\n').Length,
                        "file"
                    )
                };
            }
            catch
            {
                return new List<CodeChunk>();
            }
        }
    }
    
    private string GetClassContext(ClassDeclarationSyntax cls)
    {
        var parts = new List<string>();
        
        // Add usings from the file
        var root = cls.SyntaxTree.GetRoot();
        var usings = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Take(10) // Limit usings to keep chunk size reasonable
            .Select(u => u.ToString())
            .ToList();
        
        if (usings.Any())
        {
            parts.Add(string.Join("\n", usings));
        }
        
        // Add namespace if exists
        var ns = cls.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns != null)
        {
            parts.Add($"namespace {ns.Name}");
            parts.Add("{");
        }
        
        // Add XML documentation
        var xmlDocs = GetXmlDocumentation(cls);
        if (!string.IsNullOrWhiteSpace(xmlDocs))
        {
            parts.Add(xmlDocs.Trim());
        }
        
        // Add class declaration with base types
        var classDecl = $"{cls.Modifiers} class {cls.Identifier}";
        if (cls.TypeParameterList != null)
        {
            classDecl += cls.TypeParameterList.ToString();
        }
        if (cls.BaseList != null)
        {
            classDecl += " : " + cls.BaseList.Types.ToString();
        }
        parts.Add(classDecl);
        parts.Add("{");
        
        // Add member signatures (not full implementations)
        var members = new List<string>();
        
        // Add field signatures
        var allFields = cls.Members.OfType<FieldDeclarationSyntax>().ToList();
        foreach (var field in allFields.Take(10))
        {
            members.Add($"    {field.Modifiers} {field.Declaration.Type} {field.Declaration.Variables};");
        }
        if (allFields.Count > 10)
        {
            members.Add($"    // ... and {allFields.Count - 10} more fields");
        }
        
        // Add property signatures
        var allProperties = cls.Members.OfType<PropertyDeclarationSyntax>().ToList();
        foreach (var prop in allProperties.Take(10))
        {
            var propSig = $"    {prop.Modifiers} {prop.Type} {prop.Identifier} {{ ";
            if (prop.AccessorList != null)
            {
                propSig += string.Join(" ", prop.AccessorList.Accessors.Select(a => a.Keyword.Text + ";"));
            }
            propSig += " }";
            members.Add(propSig);
        }
        if (allProperties.Count > 10)
        {
            members.Add($"    // ... and {allProperties.Count - 10} more properties");
        }
        
        // Add method signatures
        var allMethods = cls.Members.OfType<MethodDeclarationSyntax>().ToList();
        foreach (var method in allMethods.Take(10))
        {
            var methodSig = $"    {method.Modifiers} {method.ReturnType} {method.Identifier}{method.ParameterList};";
            members.Add(methodSig);
        }
        if (allMethods.Count > 10)
        {
            members.Add($"    // ... and {allMethods.Count - 10} more methods");
        }
        
        if (members.Any())
        {
            parts.AddRange(members);
        }
        else
        {
            parts.Add("    // Members...");
        }
        
        parts.Add("}");
        
        if (ns != null)
        {
            parts.Add("}");
        }
        
        return string.Join("\n", parts);
    }
    
    private string GetInterfaceContext(InterfaceDeclarationSyntax iface)
    {
        var parts = new List<string>();
        
        // Add XML documentation
        var xmlDocs = GetXmlDocumentation(iface);
        if (!string.IsNullOrWhiteSpace(xmlDocs))
        {
            parts.Add(xmlDocs.Trim());
        }
        
        // Add interface declaration
        var ifaceDecl = $"{iface.Modifiers} interface {iface.Identifier}";
        if (iface.TypeParameterList != null)
        {
            ifaceDecl += iface.TypeParameterList.ToString();
        }
        if (iface.BaseList != null)
        {
            ifaceDecl += " : " + iface.BaseList.Types.ToString();
        }
        parts.Add(ifaceDecl);
        parts.Add("{");
        
        // Add member signatures
        foreach (var member in iface.Members.Take(20))
        {
            parts.Add($"    {member.ToString().Trim()}");
        }
        
        parts.Add("}");
        
        return string.Join("\n", parts);
    }
    
    private string GetRecordContext(RecordDeclarationSyntax record)
    {
        var parts = new List<string>();
        
        // Add XML documentation
        var xmlDocs = GetXmlDocumentation(record);
        if (!string.IsNullOrWhiteSpace(xmlDocs))
        {
            parts.Add(xmlDocs.Trim());
        }
        
        // For simple records, return the full declaration
        var recordStr = record.ToString();
        if (recordStr.Length < MaxChunkSize / 2)
        {
            return recordStr;
        }
        
        // For complex records, return signature only
        var recordDecl = $"{record.Modifiers} record {record.Identifier}";
        if (record.TypeParameterList != null)
        {
            recordDecl += record.TypeParameterList.ToString();
        }
        if (record.ParameterList != null)
        {
            recordDecl += record.ParameterList.ToString();
        }
        if (record.BaseList != null)
        {
            recordDecl += " : " + record.BaseList.Types.ToString();
        }
        
        return recordDecl + ";";
    }
    
    private List<CodeChunk> GetMethodChunks(MethodDeclarationSyntax method, string filePath)
    {
        var chunks = new List<CodeChunk>();
        var methodStr = method.ToFullString().Trim();
        
        // Always create the primary method chunk (with smart truncation if needed)
        var primaryMethodChunk = GetMethodWithContext(method);
        chunks.Add(CreateChunk(primaryMethodChunk, method, filePath, "method"));
        
        // For large methods, also create sliding window chunks of the method body
        if (methodStr.Length > _slidingWindowConfig.TargetChunkSize)
        {
            var bodyChunks = CreateMethodBodyChunks(method, filePath);
            chunks.AddRange(bodyChunks);
        }
        
        return chunks;
    }
    
    private string GetMethodWithContext(MethodDeclarationSyntax method)
    {
        var containingType = method.Ancestors()
            .FirstOrDefault(a => a is TypeDeclarationSyntax) as TypeDeclarationSyntax;
        
        if (containingType == null)
            return method.ToFullString();
        
        var parts = new List<string>();
        
        // Add minimal type context
        var typeKind = containingType switch
        {
            ClassDeclarationSyntax => "class",
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax => "record",
            StructDeclarationSyntax => "struct",
            _ => "type"
        };
        
        parts.Add($"// In {typeKind} {containingType.Identifier}");
        
        // Add the full method (includes XML documentation as leading trivia)
        var methodStr = method.ToFullString().Trim();
        
        // If method is too large, do smart truncation preserving beginning
        if (methodStr.Length > MaxChunkSize)
        {
            // Calculate available space for method content after context
            var currentContextSize = string.Join("\n", parts).Length;
            var availableSpace = MaxChunkSize - currentContextSize - 200; // Reserve 200 chars for truncation message
            
            if (availableSpace > 500) // Only truncate if we have meaningful space left
            {
                // Extract method signature and beginning of body
                var methodLines = methodStr.Split('\n');
                var truncatedLines = new List<string>();
                var currentSize = 0;
                
                // Add lines until we hit the size limit
                foreach (var line in methodLines)
                {
                    var lineSize = line.Length + 1; // +1 for newline
                    if (currentSize + lineSize > availableSpace)
                    {
                        break;
                    }
                    truncatedLines.Add(line);
                    currentSize += lineSize;
                }
                
                // Add the truncated content with clear indication
                if (truncatedLines.Count > 0)
                {
                    parts.AddRange(truncatedLines);
                    parts.Add("");
                    parts.Add($"    // ... METHOD BODY TRUNCATED ({methodStr.Length - currentSize} chars remaining) ...");
                    parts.Add($"    // Original method: {methodLines.Length} lines, showing first {truncatedLines.Count} lines");
                    
                    // Close method if we haven't reached the closing brace
                    if (!truncatedLines.LastOrDefault()?.Trim().Equals("}") == true)
                    {
                        parts.Add("}");
                    }
                }
                else
                {
                    // Fallback: just signature if no room for content
                    var signature = $"{method.Modifiers} {method.ReturnType} {method.Identifier}{method.ParameterList}";
                    parts.Add(signature);
                    parts.Add("{");
                    parts.Add($"    // Method too large for context ({methodStr.Length} chars) - signature only");
                    parts.Add("}");
                }
            }
            else
            {
                // Very little space left - just signature
                var signature = $"{method.Modifiers} {method.ReturnType} {method.Identifier}{method.ParameterList}";
                parts.Add(signature + $"; // Method body too large ({methodStr.Length} chars)");
            }
        }
        else
        {
            parts.Add(methodStr);
        }
        
        return string.Join("\n", parts);
    }
    
    private List<CodeChunk> CreateMethodBodyChunks(MethodDeclarationSyntax method, string filePath)
    {
        var chunks = new List<CodeChunk>();
        var methodBody = method.Body?.ToString();
        
        if (string.IsNullOrEmpty(methodBody))
        {
            return chunks; // No body to chunk (e.g., abstract methods, interface methods)
        }
        
        // Get method context for each chunk
        var containingType = method.Ancestors()
            .FirstOrDefault(a => a is TypeDeclarationSyntax) as TypeDeclarationSyntax;
        var methodSignature = $"{method.Modifiers} {method.ReturnType} {method.Identifier}{method.ParameterList}";
        var containingClass = containingType?.Identifier.ToString() ?? "UnknownClass";
        
        // Split method body into lines
        var bodyLines = methodBody.Split('\n');
        var targetChunkSize = _slidingWindowConfig.TargetChunkSize;
        var overlapPercentage = _slidingWindowConfig.OverlapPercentage;
        
        var currentLine = 0;
        var chunkIndex = 1;
        
        while (currentLine < bodyLines.Length)
        {
            var chunkLines = new List<string>();
            var currentSize = 0;
            var startLine = currentLine;
            
            // Reserve space for method context header (signature, class info)
            var contextHeader = BuildMethodChunkHeader(methodSignature, containingClass, chunkIndex, bodyLines.Length);
            var headerSize = contextHeader.Length;
            var availableSpace = targetChunkSize - headerSize;
            
            // Build chunk content respecting size limits
            while (currentLine < bodyLines.Length && currentSize < availableSpace)
            {
                var line = bodyLines[currentLine];
                var lineSize = line.Length + 1; // +1 for newline
                
                // Check for good breaking points when approaching limit
                if (currentSize + lineSize > availableSpace && chunkLines.Count > 0)
                {
                    if (IsGoodMethodBreakingPoint(line) || currentSize > availableSpace * 0.8)
                    {
                        break;
                    }
                }
                
                chunkLines.Add(line);
                currentSize += lineSize;
                currentLine++;
            }
            
            // Create chunk if we have content
            if (chunkLines.Count > 0)
            {
                var chunkContent = contextHeader + "\n" + string.Join("\n", chunkLines);
                
                // Calculate actual line numbers in the original file
                var methodStartLine = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var bodyStartLine = method.Body?.GetLocation().GetLineSpan().StartLinePosition.Line + 1 ?? methodStartLine;
                var chunkStartLine = bodyStartLine + startLine;
                var chunkEndLine = bodyStartLine + currentLine - 1;
                
                var chunk = new CodeChunk(
                    $"{filePath}:{chunkStartLine}:method-body-{chunkIndex}",
                    chunkContent.Trim(),
                    filePath,
                    chunkStartLine,
                    chunkEndLine,
                    "method-body"
                );
                
                chunks.Add(chunk);
                chunkIndex++;
            }
            
            // Calculate overlap for next chunk
            if (currentLine < bodyLines.Length)
            {
                var overlapLines = Math.Min((int)(chunkLines.Count * overlapPercentage), _slidingWindowConfig.MaxOverlapLines);
                currentLine = Math.Max(startLine + chunkLines.Count - overlapLines, startLine + 1);
            }
        }
        
        return chunks;
    }
    
    private string BuildMethodChunkHeader(string methodSignature, string containingClass, int chunkIndex, int totalBodyLines)
    {
        var header = new System.Text.StringBuilder();
        header.AppendLine($"// Method body chunk {chunkIndex} from: {methodSignature}");
        header.AppendLine($"// Class: {containingClass}");
        //header.AppendLine($"// Body lines: {totalBodyLines} total");
        header.AppendLine();
        return header.ToString();
    }
    
    private static bool IsGoodMethodBreakingPoint(string line)
    {
        var trimmed = line.Trim();
        
        // Good breaking points within method bodies
        return trimmed.Length == 0 ||                    // Empty line
               trimmed.StartsWith("//") ||               // Comment
               trimmed.StartsWith("/*") ||               // Block comment
               trimmed == "}" ||                         // Closing brace
               trimmed.StartsWith("if (") ||             // Control flow
               trimmed.StartsWith("else") ||             
               trimmed.StartsWith("for (") ||            
               trimmed.StartsWith("while (") ||          
               trimmed.StartsWith("foreach (") ||        
               trimmed.StartsWith("switch (") ||         
               trimmed.StartsWith("case ") ||            // Switch cases
               trimmed.StartsWith("default:") ||         
               trimmed.StartsWith("try") ||              // Exception handling
               trimmed.StartsWith("catch") ||            
               trimmed.StartsWith("finally") ||          
               trimmed.StartsWith("using (") ||          // Resource management
               trimmed.StartsWith("var ") ||             // Variable declarations
               trimmed.StartsWith("string ") ||          
               trimmed.StartsWith("int ") ||             
               trimmed.StartsWith("bool ") ||            
               trimmed.StartsWith("async ") ||           
               trimmed.StartsWith("await ") ||           
               trimmed.Contains(" = new ") ||            // Object instantiation
               trimmed.StartsWith("return ") ||          // Return statements
               trimmed.StartsWith("throw ") ||           // Exception throwing
               trimmed.StartsWith("#region") ||          // Region markers
               trimmed.StartsWith("#endregion") ||       
               trimmed.Contains("TODO:") ||              // Development markers
               trimmed.Contains("FIXME:") ||             
               trimmed.Contains("NOTE:");                
    }
    
    private string GetPropertyWithContext(PropertyDeclarationSyntax prop)
    {
        var containingType = prop.Ancestors()
            .FirstOrDefault(a => a is TypeDeclarationSyntax) as TypeDeclarationSyntax;
        
        var parts = new List<string>();
        
        if (containingType != null)
        {
            parts.Add($"// In class {containingType.Identifier}");
        }
        
        // Add the property (includes XML documentation as leading trivia)
        parts.Add(prop.ToFullString().Trim());
        
        return string.Join("\n", parts);
    }
    
    private string GetLocalFunctionWithContext(LocalFunctionStatementSyntax localFunction)
    {
        var containingMethod = localFunction.Ancestors()
            .FirstOrDefault(a => a is MethodDeclarationSyntax) as MethodDeclarationSyntax;
        
        var parts = new List<string>();
        
        if (containingMethod != null)
        {
            parts.Add($"// Local function in method {containingMethod.Identifier}");
        }
        else
        {
            parts.Add("// Local function");
        }
        
        // Add the local function
        parts.Add(localFunction.ToFullString().Trim());
        
        return string.Join("\n", parts);
    }
    
    private string GetSwitchExpressionWithContext(SwitchExpressionSyntax switchExpression)
    {
        var containingMethod = switchExpression.Ancestors()
            .FirstOrDefault(a => a is MethodDeclarationSyntax || a is PropertyDeclarationSyntax);
        
        var parts = new List<string>();
        
        if (containingMethod != null)
        {
            var contextName = containingMethod switch
            {
                MethodDeclarationSyntax method => $"method {method.Identifier}",
                PropertyDeclarationSyntax prop => $"property {prop.Identifier}",
                _ => "member"
            };
            parts.Add($"// Switch expression in {contextName}");
        }
        else
        {
            parts.Add("// Switch expression (pattern matching)");
        }
        
        // Add the switch expression with some context
        var switchStr = switchExpression.ToFullString().Trim();
        if (switchStr.Length > 500) // Truncate very long switch expressions
        {
            var lines = switchStr.Split('\n');
            if (lines.Length > 10)
            {
                parts.AddRange(lines.Take(10));
                parts.Add("    // ... more cases ...");
            }
            else
            {
                parts.Add(switchStr);
            }
        }
        else
        {
            parts.Add(switchStr);
        }
        
        return string.Join("\n", parts);
    }
    
    private string GetXmlDocumentation(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || 
                        t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .ToList();
        
        if (!trivia.Any())
            return "";
        
        return string.Join("\n", trivia.Select(t => t.ToString().Trim()));
    }
    
    private CodeChunk CreateChunk(string content, SyntaxNode node, string filePath, string chunkType)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        var startLine = lineSpan.StartLinePosition.Line + 1;
        var endLine = lineSpan.EndLinePosition.Line + 1;
        
        // Detect authentication patterns and enhance chunk type
        var enhancedChunkType = DetectAuthenticationPattern(content, chunkType, filePath);
        
        if (enhancedChunkType != chunkType)
        {
#if DEBUG
            Console.Error.WriteLine($"[DEBUG] CreateChunk: ChunkType enhanced from '{chunkType}' to '{enhancedChunkType}'");
#endif
        }
        
        // Add file path context if requested
        if (_includeFilePath || _includeProjectContext)
        {
            var contextPrefix = BuildContextPrefix(filePath);
            content = $"{contextPrefix}\n{content}";
        }
        
        // Ensure content isn't too large
        if (content.Length > MaxChunkSize)
        {
            content = content.Substring(0, MaxChunkSize) + "\n// ... truncated";
        }
        
        return new CodeChunk(
            $"{filePath}:{startLine}",
            content.Trim(),
            filePath,
            startLine,
            endLine,
            enhancedChunkType
        );
    }
    
    private string BuildContextPrefix(string filePath)
    {
        var contextParts = new List<string>();
        
        if (_includeFilePath)
        {
            // Get relative path from project root, preferring meaningful relative paths
            var relativePath = GetProjectRelativePath(filePath);
            contextParts.Add($"// File: {relativePath}");
        }
        
        if (_includeProjectContext)
        {
            // Add project context like namespace, folder structure
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                var folderName = Path.GetFileName(directory);
                if (!string.IsNullOrEmpty(folderName))
                {
                    contextParts.Add($"// Directory: {folderName}");
                }
            }
        }
        
        return string.Join("\n", contextParts);
    }
    
    private string GetProjectRelativePath(string filePath)
    {
        // Try to find project root markers (.csproj, .sln, src/, etc.)
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return Path.GetFileName(filePath);
        }
        
        // Look for project root indicators
        var currentDir = directory;
        string? projectRoot = null;
        
        while (!string.IsNullOrEmpty(currentDir))
        {
            // Check for common project root indicators
            if (Directory.GetFiles(currentDir, "*.csproj").Any() ||
                Directory.GetFiles(currentDir, "*.sln").Any() ||
                Path.GetFileName(currentDir).Equals("src", StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(Path.Combine(currentDir, ".git")))
            {
                projectRoot = currentDir;
                break;
            }
            
            var parent = Path.GetDirectoryName(currentDir);
            if (parent == currentDir) break; // Reached root
            currentDir = parent;
        }
        
        // If project root found, return relative path from there
        if (!string.IsNullOrEmpty(projectRoot))
        {
            try
            {
                var relativePath = Path.GetRelativePath(projectRoot, filePath);
                return relativePath;
            }
            catch
            {
                // Fallback to filename if relative path fails
                return Path.GetFileName(filePath);
            }
        }
        
        // Fallback: try relative to current working directory
        try
        {
            var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, filePath);
            if (!relativePath.StartsWith(".."))
            {
                return relativePath;
            }
        }
        catch
        {
            // Ignore path errors
        }
        
        // Final fallback: just the filename
        return Path.GetFileName(filePath);
    }
    
    /// <summary>
    /// Extract top-level statements from Program.cs style files
    /// </summary>
    private List<CodeChunk> ExtractTopLevelStatements(SyntaxNode root, string filePath)
    {
        var chunks = new List<CodeChunk>();
        var globalStatements = root.DescendantNodes()
            .OfType<GlobalStatementSyntax>()
            .ToList();
            
        if (!globalStatements.Any()) return chunks;
        
        // Group consecutive statements into logical chunks
        var currentChunk = new List<GlobalStatementSyntax>();
        var lastLine = -1;
        
        foreach (var statement in globalStatements)
        {
            var startLine = statement.GetLocation().GetLineSpan().StartLinePosition.Line;
            
            // If there's a gap of more than 2 lines, start a new chunk
            if (lastLine != -1 && startLine - lastLine > 2)
            {
                if (currentChunk.Any())
                {
                    chunks.Add(CreateTopLevelChunk(currentChunk, filePath));
                    currentChunk = new List<GlobalStatementSyntax>();
                }
            }
            
            currentChunk.Add(statement);
            lastLine = statement.GetLocation().GetLineSpan().EndLinePosition.Line;
        }
        
        // Add the last chunk
        if (currentChunk.Any())
        {
            chunks.Add(CreateTopLevelChunk(currentChunk, filePath));
        }
        
        return chunks;
    }
    
    private CodeChunk CreateTopLevelChunk(List<GlobalStatementSyntax> statements, string filePath)
    {
        var firstStatement = statements.First();
        var lastStatement = statements.Last();
        
        var startLine = firstStatement.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var endLine = lastStatement.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
        
        var content = string.Join("\n", statements.Select(s => s.ToString()));
        
        // Add context for top-level programs
        var contextualContent = $"// Top-level program statements\n{content}";
        
        if (_includeFilePath)
        {
            var contextPrefix = BuildContextPrefix(filePath);
            contextualContent = $"{contextPrefix}\n{contextualContent}";
        }
        
        return new CodeChunk(
            $"{filePath}:{startLine}",
            contextualContent.Trim(),
            filePath,
            startLine,
            endLine,
            "top_level_statements"
        );
    }
    
    /// <summary>
    /// Intelligent sliding window chunking for files with no clear structure
    /// </summary>
    private List<CodeChunk> ChunkFileWithSlidingWindow(string code, string filePath)
    {
        var chunks = new List<CodeChunk>();
        var lines = code.Split('\n');
        
        if (lines.Length == 0) return chunks;
        
        // If file is small enough, return as single chunk
        // Use sliding window target size to decide when to split, not the embedding limit
        if (code.Length <= _slidingWindowConfig.TargetChunkSize)
        {
            chunks.Add(new CodeChunk(
                $"{filePath}:1",
                AddFilePathContext(code, filePath),
                filePath,
                1,
                lines.Length,
                "file"
            ));
            return chunks;
        }
        
        // Configuration for sliding window - now completely separate from MaxChunkSize
        var targetChunkSize = _slidingWindowConfig.TargetChunkSize;
        
        var currentLine = 0;
        var chunkNumber = 1;
        
        while (currentLine < lines.Length)
        {
            var chunkLines = new List<string>();
            var currentSize = 0;
            var startLine = currentLine;
            
            // Build chunk respecting intelligent boundaries
            while (currentLine < lines.Length && currentSize < targetChunkSize)
            {
                var line = lines[currentLine];
                var lineSize = line.Length + 1; // +1 for newline
                
                // If adding this line would exceed target, check for good breaking point
                if (currentSize + lineSize > targetChunkSize && chunkLines.Count > 0)
                {
                    // Look for intelligent breaking points
                    if (IsGoodBreakingPoint(line) || currentSize > targetChunkSize * 0.5)
                    {
                        break;
                    }
                }
                
                chunkLines.Add(line);
                currentSize += lineSize;
                currentLine++;
            }
            
            // Create chunk with content
            if (chunkLines.Count > 0)
            {
                var chunkContent = string.Join("\n", chunkLines);
                var contextualContent = AddFilePathContext(chunkContent, filePath);
                
                // Add chunk metadata if file will have multiple chunks
                if (code.Length > _slidingWindowConfig.TargetChunkSize) // Only for files that get chunked
                {
                    contextualContent = $"// Chunk {chunkNumber} of file\n{contextualContent}";
                }
                
                chunks.Add(new CodeChunk(
                    $"{filePath}:{startLine + 1}",
                    contextualContent,
                    filePath,
                    startLine + 1,
                    startLine + chunkLines.Count,
                    "sliding_window"
                ));
                
                chunkNumber++;
            }
            
            // Calculate overlap for next chunk using configuration
            if (currentLine < lines.Length)
            {
                var overlapLines = Math.Min((int)(chunkLines.Count * _slidingWindowConfig.OverlapPercentage), _slidingWindowConfig.MaxOverlapLines);
                currentLine = Math.Max(startLine + chunkLines.Count - overlapLines, startLine + 1);
            }
        }
        
        return chunks;
    }
    
    /// <summary>
    /// Determines if a line is a good place to break a chunk
    /// </summary>
    private static bool IsGoodBreakingPoint(string line)
    {
        var trimmed = line.Trim();
        
        // Good breaking points
        return trimmed.Length == 0 || // Empty line
               trimmed.StartsWith("//") || // Comment
               trimmed.StartsWith("/*") ||
               trimmed.StartsWith("using ") || // Using statement
               trimmed.StartsWith("namespace ") || // Namespace
               trimmed.StartsWith("public ") || // Public declaration
               trimmed.StartsWith("private ") || // Private declaration
               trimmed.StartsWith("protected ") || // Protected declaration
               trimmed.StartsWith("internal ") || // Internal declaration
               trimmed.StartsWith("class ") || // Class declaration
               trimmed.StartsWith("interface ") || // Interface declaration
               trimmed.StartsWith("struct ") || // Struct declaration
               trimmed.StartsWith("enum ") || // Enum declaration
               trimmed == "}" || // Closing brace
               trimmed.StartsWith("public") || // Any public member
               trimmed.StartsWith("#region") || // Region start
               trimmed.StartsWith("#endregion"); // Region end
    }
    
    /// <summary>
    /// Adds file path context to content if enabled
    /// </summary>
    private string AddFilePathContext(string content, string filePath)
    {
        if (_includeFilePath || _includeProjectContext)
        {
            var contextPrefix = BuildContextPrefix(filePath);
            return $"{contextPrefix}\n{content}";
        }
        return content;
    }
    
    /// <summary>
    /// Determines if a file is auto-generated based on file name and content
    /// </summary>
    private static bool IsGeneratedFile(string filePath, string content)
    {
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        
        // Check file name patterns
        if (fileName.EndsWith("Reference.cs") ||
            fileName.EndsWith("ModelSnapshot.cs") ||
            //fileName.EndsWith(".generated.cs") ||
            fileName.EndsWith(".designer.cs") ||
            fileName.EndsWith(".g.cs") ||
            fileName.EndsWith(".g.i.cs") ||
            fileName.Contains("assemblyinfo.cs") ||
            fileName.Contains("assemblyattributes.cs"))
        {
            return true;
        }
        
        // Check content markers
        var firstLines = content.Split('\n').Take(10);
        foreach (var line in firstLines)
        {
            if (line.Contains("<auto-generated") ||
                line.Contains("This code was generated") ||
                line.Contains("do not modify") ||
                line.Contains("autogenerated"))
            {
                return true;
            }
        }
        
        return false;
    }
    
    private List<CodeChunk> ValidateAndDeduplicateChunks(List<CodeChunk> chunks)
    {
        var deduped = new List<CodeChunk>();
        var seenContent = new HashSet<string>();
        
        foreach (var chunk in chunks.OrderBy(c => c.StartLine).ThenByDescending(c => c.EndLine))
        {
            // Skip if we've seen this exact content
            if (seenContent.Contains(chunk.Content))
                continue;
            
            // Skip if this chunk is fully contained within another of the same type
            // Don't skip methods/classes just because they're inside a namespace!
            bool isContained = deduped.Any(existing => 
                chunk.FilePath == existing.FilePath &&
                chunk.StartLine >= existing.StartLine && 
                chunk.EndLine <= existing.EndLine &&
                chunk.ChunkType == existing.ChunkType && // Only check containment for same types
                chunk.Id != existing.Id);
                
            if (!isContained)
            {
                deduped.Add(chunk);
                seenContent.Add(chunk.Content);
            }
        }
        
        // Warn if file generated too many chunks
        if (deduped.Count > 50)
        {
            Console.Error.WriteLine($"[RoslynParser] Warning: {chunks.First().FilePath} generated {deduped.Count} chunks");
        }
        
        return deduped;
    }
    
    private List<CodeChunk> ParseRazorFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var chunks = new List<CodeChunk>();
            var lines = content.Split('\n');
            
            // Track sections with their actual line numbers
            var usings = new List<(string content, int startLine, int endLine)>();
            var htmlSections = new List<(string content, int startLine, int endLine)>();
            var codeSections = new List<(string content, int startLine, int endLine)>();
            
            int lineIndex = 0;
            while (lineIndex < lines.Length)
            {
                var line = lines[lineIndex].Trim();
                
                // Extract @using directives
                if (line.StartsWith("@using "))
                {
                    usings.Add((line, lineIndex + 1, lineIndex + 1)); // +1 for 1-based line numbers
                    lineIndex++;
                    continue;
                }
                
                // Extract @code blocks
                if (line.StartsWith("@code"))
                {
                    int blockStartLine = lineIndex + 1; // +1 for 1-based line numbers
                    var codeBlock = ExtractCodeBlock(lines, ref lineIndex);
                    int blockEndLine = lineIndex; // lineIndex is already at the line after the block
                    if (!string.IsNullOrWhiteSpace(codeBlock))
                    {
                        codeSections.Add((codeBlock, blockStartLine, blockEndLine));
                    }
                    continue;
                }
                
                // Extract @functions blocks
                if (line.StartsWith("@functions"))
                {
                    int blockStartLine = lineIndex + 1; // +1 for 1-based line numbers
                    var functionsBlock = ExtractCodeBlock(lines, ref lineIndex);
                    int blockEndLine = lineIndex; // lineIndex is already at the line after the block
                    if (!string.IsNullOrWhiteSpace(functionsBlock))
                    {
                        codeSections.Add((functionsBlock, blockStartLine, blockEndLine));
                    }
                    continue;
                }
                
                // Collect HTML/Razor markup sections
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("@") || line.Contains("@bind") || line.Contains("@onclick"))
                {
                    int sectionStartLine = lineIndex + 1; // +1 for 1-based line numbers
                    var htmlSection = ExtractHtmlSection(lines, ref lineIndex);
                    int sectionEndLine = lineIndex; // lineIndex is already at the line after the section
                    if (!string.IsNullOrWhiteSpace(htmlSection))
                    {
                        htmlSections.Add((htmlSection, sectionStartLine, sectionEndLine));
                    }
                    continue;
                }
                
                lineIndex++;
            }
            
            // Create chunks for different sections
            
            // NOTE: @using directive chunking removed to eliminate semantic noise
            // Using directives provide minimal semantic value for business logic search
            
            // 2. Parse @code sections using smart chunking (similar to C# methods)
            int codeIndex = 1;
            foreach (var (codeSection, sectionStartLine, sectionEndLine) in codeSections)
            {
                chunks.AddRange(CreateRazorCodeChunks(codeSection, filePath, sectionStartLine, sectionEndLine, codeIndex));
                codeIndex++;
            }
            
            // 3. Create smart chunks for HTML/Razor sections
            foreach (var (htmlSection, startLine, endLine) in htmlSections.Where(h => h.content.Length > 100)) // Only significant sections
            {
                chunks.AddRange(CreateRazorHtmlChunks(htmlSection, filePath, startLine, endLine));
            }
            
            // If no chunks were created, add the whole file as fallback
            if (!chunks.Any())
            {
                chunks.Add(new CodeChunk(
                    $"{filePath}:1",
                    content.Length > MaxChunkSize ? content.Substring(0, MaxChunkSize) + "\n<!-- ... truncated -->" : content,
                    filePath,
                    1,
                    lines.Length,
                    "razor-file"
                ));
            }
            
            return chunks;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoslynParser] Error parsing Razor file {filePath}: {ex.Message}");
            
            // Return whole file as fallback
            try
            {
                var content = File.ReadAllText(filePath);
                return new List<CodeChunk>
                {
                    new CodeChunk(
                        $"{filePath}:1",
                        content.Length > MaxChunkSize ? content.Substring(0, MaxChunkSize) + "\n<!-- ... truncated -->" : content,
                        filePath,
                        1,
                        content.Split('\n').Length,
                        "razor-file"
                    )
                };
            }
            catch
            {
                return new List<CodeChunk>();
            }
        }
    }
    
    private string ExtractCodeBlock(string[] lines, ref int lineIndex)
    {
        var codeLines = new List<string>();
        var startLine = lineIndex;
        
        // Skip the @code { line
        lineIndex++;
        if (lineIndex < lines.Length && lines[lineIndex].Trim() == "{")
        {
            lineIndex++;
        }
        
        int braceCount = 1;
        while (lineIndex < lines.Length && braceCount > 0)
        {
            var line = lines[lineIndex];
            
            // Count braces to find the end of the block
            foreach (char c in line)
            {
                if (c == '{') braceCount++;
                if (c == '}') braceCount--;
            }
            
            if (braceCount > 0) // Don't include the closing brace
            {
                codeLines.Add(line);
            }
            
            lineIndex++;
        }
        
        return string.Join("\n", codeLines);
    }
    
    private string ExtractHtmlSection(string[] lines, ref int lineIndex)
    {
        var htmlLines = new List<string>();
        var startLine = lineIndex;
        
        // Collect consecutive HTML/Razor lines
        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            
            // Stop at @code, @functions, or empty lines
            if (line.Trim().StartsWith("@code") || 
                line.Trim().StartsWith("@functions") || 
                string.IsNullOrWhiteSpace(line.Trim()))
            {
                break;
            }
            
            htmlLines.Add(line);
            lineIndex++;
            
            // Limit section size
            if (htmlLines.Count > 50)
            {
                break;
            }
        }
        
        return string.Join("\n", htmlLines);
    }
    
    private string GetRazorMethodWithContext(MethodDeclarationSyntax method, string filePath)
    {
        var parts = new List<string>();
        
        parts.Add($"// Method in Razor component {Path.GetFileName(filePath)}");
        
        // Add the method (includes XML documentation as leading trivia)
        parts.Add(method.ToFullString().Trim());
        
        return string.Join("\n", parts);
    }
    
    private string GetRazorPropertyWithContext(PropertyDeclarationSyntax prop, string filePath)
    {
        var parts = new List<string>();
        
        // Add the property (includes XML documentation as leading trivia)
        parts.Add(prop.ToFullString().Trim());
        
        return string.Join("\n", parts);
    }
    
    private CodeChunk CreateRazorChunk(string content, SyntaxNode node, string filePath, string chunkType, int index, int sectionStartLine = 0)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        // Add the section's start line offset to get the actual file line numbers
        // Note: lineSpan is 0-based, sectionStartLine is 1-based from ParseRazorFile
        // Need to add 1 to convert from 0-based to 1-based line numbers
        var startLine = lineSpan.StartLinePosition.Line + 1 + sectionStartLine;
        var endLine = lineSpan.EndLinePosition.Line + 1 + sectionStartLine;
        
        // Validate line numbers
        if (startLine <= 0)
        {
            // Log warning and adjust to minimum valid line number
            Console.WriteLine($"Warning: Invalid start line {startLine} for {chunkType} in {filePath}. Adjusting to 1.");
            startLine = 1;
        }
        
        if (endLine < startLine)
        {
            // Log warning and adjust end line
            Console.WriteLine($"Warning: Invalid end line {endLine} < start line {startLine} for {chunkType} in {filePath}. Adjusting to match start line.");
            endLine = startLine;
        }
        
        // Additional validation: Check if line numbers seem reasonable
        // Most Razor files won't exceed 10000 lines
        if (startLine > 10000 || endLine > 10000)
        {
            Console.WriteLine($"Warning: Suspicious line numbers (start: {startLine}, end: {endLine}) for {chunkType} in {filePath}");
        }
        
        // Ensure content isn't too large
        if (content.Length > MaxChunkSize)
        {
            content = content.Substring(0, MaxChunkSize) + "\n// ... truncated";
        }
        
        // Use index to ensure unique IDs even if multiple elements are at the same line
        return new CodeChunk(
            $"{filePath}:{startLine}:{chunkType}{index}",
            content.Trim(),
            filePath,
            startLine,
            endLine,
            chunkType
        );
    }
    
    private string DetectAuthenticationPattern(string content, string chunkType, string filePath)
    {
        var contentLower = content.ToLowerInvariant();
        var fileNameLower = Path.GetFileName(filePath).ToLowerInvariant();
        var directoryLower = Path.GetDirectoryName(filePath)?.ToLowerInvariant() ?? "";
        
        // Authentication-specific patterns
        var authPatterns = new[] {
            "authenticate", "authorize", "login", "logout", "signin", "signout",
            "saml", "oauth", "jwt", "bearer", "claims", "identity", "principal",
            "certificate", "token", "session", "cookie", "credential",
            "[authorize]", "[authentication]", "authenticationhandler",
            "iauthorizationhandler", "authenticationscheme", "claimsprincipal"
        };
        
        var hasAuthPattern = authPatterns.Any(pattern => contentLower.Contains(pattern));
        var isAuthFile = fileNameLower.Contains("auth") || 
                        fileNameLower.Contains("login") || 
                        fileNameLower.Contains("security") ||
                        directoryLower.Contains("identity") ||
                        directoryLower.Contains("auth") ||
                        directoryLower.Contains("security");
        
        // Security-specific patterns
        var securityPatterns = new[] {
            "encryption", "decrypt", "hash", "salt", "cryptography",
            "certificate", "x509", "rsa", "aes", "hmac"
        };
        
        var hasSecurityPattern = securityPatterns.Any(pattern => contentLower.Contains(pattern));
        
        // Enhanced chunk types for authentication/security code
        if (hasAuthPattern || isAuthFile)
        {
            var enhancedType = chunkType switch
            {
                "class" => "class-auth",
                "method" => "method-auth",
                "interface" => "interface-auth",
                "property" => "property-auth",
                _ => $"{chunkType}-auth"
            };
#if DEBUG
            Console.Error.WriteLine($"[DEBUG] Auth pattern detected! Original: '{chunkType}' -> Enhanced: '{enhancedType}' for {Path.GetFileName(filePath)}");
#endif
            return enhancedType;
        }
        
        if (hasSecurityPattern)
        {
            return chunkType switch
            {
                "class" => "class-security",
                "method" => "method-security",
                _ => $"{chunkType}-security"
            };
        }
        
        // Configuration patterns
        if (fileNameLower.Contains("program.cs") || fileNameLower.Contains("startup.cs"))
        {
            return $"{chunkType}-config";
        }
        
        // Controller patterns
        if (chunkType == "class" && (contentLower.Contains("controller") || fileNameLower.Contains("controller")))
        {
            return "class-controller";
        }
        
        // Service patterns
        if (chunkType == "class" && (contentLower.Contains("service") || directoryLower.Contains("services")))
        {
            return "class-service";
        }
        
        return chunkType;
    }
    
    /// <summary>
    /// Creates smart chunks for Razor @code sections using hybrid approach
    /// </summary>
    private List<CodeChunk> CreateRazorCodeChunks(string codeSection, string filePath, int sectionStartLine, int sectionEndLine, int codeIndex)
    {
        var chunks = new List<CodeChunk>();
        
        try
        {
            var codeTree = CSharpSyntaxTree.ParseText(codeSection);
            var codeRoot = codeTree.GetRoot();
            
            // Strategy 1: Create primary @code chunk (complete context)
            var primaryCodeContent = $"// Razor @code block in {Path.GetFileName(filePath)}\n{codeSection}";
            
            // Smart truncation if too large, preserving beginning
            if (primaryCodeContent.Length > MaxChunkSize)
            {
                var truncationPoint = MaxChunkSize - 200; // Reserve space for truncation message
                var truncatedContent = primaryCodeContent.Substring(0, truncationPoint);
                
                // Try to break at a sensible point
                var lastLineBreak = truncatedContent.LastIndexOf('\n');
                if (lastLineBreak > truncationPoint * 0.8) // If we can get at least 80% by breaking at line
                {
                    truncatedContent = truncatedContent.Substring(0, lastLineBreak);
                }
                
                primaryCodeContent = truncatedContent + "\n\n// ... @CODE BLOCK TRUNCATED ..." + 
                                   $"\n// Original size: {codeSection.Length} chars, showing first {truncatedContent.Length} chars" +
                                   "\n}"; // Close the @code block
            }
            
            chunks.Add(new CodeChunk(
                $"{filePath}:{sectionStartLine}:razor-code-{codeIndex}",
                primaryCodeContent,
                filePath,
                sectionStartLine,
                sectionEndLine,
                "razor-code"
            ));
            
            // Strategy 2: Extract only substantial methods (>1KB) to avoid noise
            var memberIndex = 1;
            
            // Only extract methods if they're large enough to warrant individual chunking
            foreach (var method in codeRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var methodContent = GetRazorMethodWithContext(method, filePath);
                
                // Only create individual method chunks for substantial methods (configurable threshold)
                if (methodContent.Length > _slidingWindowConfig.TargetChunkSize)
                {
                    chunks.Add(CreateRazorChunk(methodContent, method, filePath, "razor-method", memberIndex, sectionStartLine));
                    
                    // Strategy 3: For large methods, create sliding window body chunks
                    if (methodContent.Length > _slidingWindowConfig.TargetChunkSize)
                    {
                        var bodyChunks = CreateRazorMethodBodyChunks(method, filePath, sectionStartLine);
                        chunks.AddRange(bodyChunks);
                    }
                }
                
                memberIndex++;
            }
            
            // NOTE: Property and field extraction removed to eliminate semantic noise
            // Properties and fields will be captured in primary @code chunks and sliding window chunks
            
            // Strategy 4: For large @code blocks, always use sliding window for comprehensive coverage
            if (codeSection.Length > _slidingWindowConfig.TargetChunkSize)
            {
                var slidingChunks = CreateRazorCodeSlidingWindowChunks(codeSection, filePath, sectionStartLine, sectionEndLine);
                chunks.AddRange(slidingChunks);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RoslynParser] Error parsing @code section in {filePath}: {ex.Message}");
            
            // Fallback: smart sliding window chunking even if parsing fails
            if (codeSection.Length > _slidingWindowConfig.TargetChunkSize)
            {
                var slidingChunks = CreateRazorCodeSlidingWindowChunks(codeSection, filePath, sectionStartLine, sectionEndLine);
                chunks.AddRange(slidingChunks);
            }
            else
            {
                // Small fallback chunk
                var fallbackContent = $"// Razor @code block in {Path.GetFileName(filePath)}\n{codeSection}";
                chunks.Add(new CodeChunk(
                    $"{filePath}:{sectionStartLine}:code{codeIndex}",
                    fallbackContent,
                    filePath,
                    sectionStartLine,
                    sectionEndLine,
                    "razor-code"
                ));
            }
        }
        
        return chunks;
    }
    
    /// <summary>
    /// Creates sliding window chunks for large Razor method bodies
    /// </summary>
    private List<CodeChunk> CreateRazorMethodBodyChunks(MethodDeclarationSyntax method, string filePath, int razorCodeStartLine)
    {
        var chunks = new List<CodeChunk>();
        var methodBody = method.Body?.ToString();
        
        if (string.IsNullOrEmpty(methodBody))
        {
            return chunks;
        }
        
        var methodSignature = $"{method.Modifiers} {method.ReturnType} {method.Identifier}{method.ParameterList}";
        var razorComponentName = Path.GetFileNameWithoutExtension(filePath);
        
        var bodyLines = methodBody.Split('\n');
        var targetChunkSize = _slidingWindowConfig.TargetChunkSize;
        var overlapPercentage = _slidingWindowConfig.OverlapPercentage;
        
        var currentLine = 0;
        var chunkIndex = 1;
        
        while (currentLine < bodyLines.Length)
        {
            var chunkLines = new List<string>();
            var currentSize = 0;
            var startLine = currentLine;
            
            // Build context header for Razor method body chunk
            var contextHeader = BuildRazorMethodChunkHeader(methodSignature, razorComponentName, chunkIndex, bodyLines.Length);
            var headerSize = contextHeader.Length;
            var availableSpace = targetChunkSize - headerSize;
            
            // Build chunk content respecting size limits
            while (currentLine < bodyLines.Length && currentSize < availableSpace)
            {
                var line = bodyLines[currentLine];
                var lineSize = line.Length + 1; // +1 for newline
                
                // Check for good breaking points when approaching limit
                if (currentSize + lineSize > availableSpace && chunkLines.Count > 0)
                {
                    if (IsGoodRazorBreakingPoint(line) || currentSize > availableSpace * 0.8)
                    {
                        break;
                    }
                }
                
                chunkLines.Add(line);
                currentSize += lineSize;
                currentLine++;
            }
            
            // Create chunk if we have content
            if (chunkLines.Count > 0)
            {
                var chunkContent = contextHeader + "\n" + string.Join("\n", chunkLines);
                
                // Calculate line numbers (approximate, since we're in a @code block)
                var chunkStartLine = razorCodeStartLine + startLine;
                var chunkEndLine = razorCodeStartLine + currentLine - 1;
                
                var chunk = new CodeChunk(
                    $"{filePath}:{chunkStartLine}:razor-method-body-{chunkIndex}",
                    chunkContent.Trim(),
                    filePath,
                    chunkStartLine,
                    chunkEndLine,
                    "razor-method-body"
                );
                
                chunks.Add(chunk);
                chunkIndex++;
            }
            
            // Calculate overlap for next chunk
            if (currentLine < bodyLines.Length)
            {
                var overlapLines = Math.Min((int)(chunkLines.Count * overlapPercentage), _slidingWindowConfig.MaxOverlapLines);
                currentLine = Math.Max(startLine + chunkLines.Count - overlapLines, startLine + 1);
            }
        }
        
        return chunks;
    }
    
    /// <summary>
    /// Creates sliding window chunks for large @code sections with no clear structure
    /// </summary>
    private List<CodeChunk> CreateRazorCodeSlidingWindowChunks(string codeSection, string filePath, int sectionStartLine, int sectionEndLine)
    {
        var chunks = new List<CodeChunk>();
        var lines = codeSection.Split('\n');
        var targetChunkSize = _slidingWindowConfig.TargetChunkSize;
        var overlapPercentage = _slidingWindowConfig.OverlapPercentage;
        
        var currentLine = 0;
        var chunkIndex = 1;
        
        while (currentLine < lines.Length)
        {
            var chunkLines = new List<string>();
            var currentSize = 0;
            var startLine = currentLine;
            
            // Build context header
            var contextHeader = $"// Razor @code chunk {chunkIndex} from: {Path.GetFileName(filePath)}\n";
            var headerSize = contextHeader.Length;
            var availableSpace = targetChunkSize - headerSize;
            
            // Build chunk content
            while (currentLine < lines.Length && currentSize < availableSpace)
            {
                var line = lines[currentLine];
                var lineSize = line.Length + 1;
                
                if (currentSize + lineSize > availableSpace && chunkLines.Count > 0)
                {
                    if (IsGoodRazorBreakingPoint(line) || currentSize > availableSpace * 0.8)
                    {
                        break;
                    }
                }
                
                chunkLines.Add(line);
                currentSize += lineSize;
                currentLine++;
            }
            
            // Create chunk if we have content
            if (chunkLines.Count > 0)
            {
                var chunkContent = contextHeader + string.Join("\n", chunkLines);
                var chunkStartLine = sectionStartLine + startLine;
                var chunkEndLine = sectionStartLine + currentLine - 1;
                
                chunks.Add(new CodeChunk(
                    $"{filePath}:{chunkStartLine}:razor-code-body-{chunkIndex}",
                    chunkContent.Trim(),
                    filePath,
                    chunkStartLine,
                    chunkEndLine,
                    "razor-code-body"
                ));
                
                chunkIndex++;
            }
            
            // Calculate overlap for next chunk
            if (currentLine < lines.Length)
            {
                var overlapLines = Math.Min((int)(chunkLines.Count * overlapPercentage), _slidingWindowConfig.MaxOverlapLines);
                currentLine = Math.Max(startLine + chunkLines.Count - overlapLines, startLine + 1);
            }
        }
        
        return chunks;
    }
    
    /// <summary>
    /// Builds context header for Razor method body chunks
    /// </summary>
    private string BuildRazorMethodChunkHeader(string methodSignature, string razorComponentName, int chunkIndex, int totalBodyLines)
    {
        var header = new System.Text.StringBuilder();
        header.AppendLine($"// Razor method body chunk {chunkIndex} from: {methodSignature}");
        header.AppendLine($"// Component: {razorComponentName}");
        // header.AppendLine($"// Body lines: {totalBodyLines} total");
        header.AppendLine();
        return header.ToString();
    }
    
    /// <summary>
    /// Determines good breaking points for Razor code chunks
    /// </summary>
    private static bool IsGoodRazorBreakingPoint(string line)
    {
        var trimmed = line.Trim();
        
        // Standard C# breaking points
        if (trimmed.Length == 0 ||                    // Empty line
            trimmed.StartsWith("//") ||               // Comment
            trimmed.StartsWith("/*") ||               // Block comment
            trimmed == "}" ||                         // Closing brace
            trimmed.StartsWith("if (") ||             // Control flow
            trimmed.StartsWith("else") ||             
            trimmed.StartsWith("for (") ||            
            trimmed.StartsWith("while (") ||          
            trimmed.StartsWith("foreach (") ||        
            trimmed.StartsWith("switch (") ||         
            trimmed.StartsWith("case ") ||            // Switch cases
            trimmed.StartsWith("try") ||              // Exception handling
            trimmed.StartsWith("catch") ||            
            trimmed.StartsWith("finally"))            
        {
            return true;
        }
        
        // Razor-specific breaking points
        return trimmed.StartsWith("[Parameter]") ||    // Blazor parameters
               trimmed.StartsWith("[Inject]") ||       // Dependency injection
               trimmed.StartsWith("protected override") ||  // Lifecycle methods
               trimmed.StartsWith("public override") ||
               trimmed.StartsWith("private void") ||   // Event handlers
               trimmed.StartsWith("public void") ||
               trimmed.StartsWith("private async") ||  // Async methods
               trimmed.StartsWith("public async");
    }
    
    /// <summary>
    /// Creates smart chunks for Razor HTML/markup sections
    /// </summary>
    private List<CodeChunk> CreateRazorHtmlChunks(string htmlSection, string filePath, int startLine, int endLine)
    {
        var chunks = new List<CodeChunk>();
        
        // For small HTML sections, create a single chunk
        if (htmlSection.Length <= _slidingWindowConfig.TargetChunkSize)
        {
            var htmlContent = $"<!-- Razor markup in {Path.GetFileName(filePath)} -->\n{htmlSection}";
            chunks.Add(new CodeChunk(
                $"{filePath}:{startLine}",
                htmlContent,
                filePath,
                startLine,
                endLine,
                "razor-html"
            ));
            return chunks;
        }
        
        // For large HTML sections, use sliding window with HTML-aware breaking points
        var lines = htmlSection.Split('\n');
        var targetChunkSize = _slidingWindowConfig.TargetChunkSize;
        var overlapPercentage = _slidingWindowConfig.OverlapPercentage;
        
        var currentLine = 0;
        var chunkIndex = 1;
        
        while (currentLine < lines.Length)
        {
            var chunkLines = new List<string>();
            var currentSize = 0;
            var chunkStartLine = currentLine;
            
            // Build context header
            var contextHeader = $"<!-- Razor HTML chunk {chunkIndex} from: {Path.GetFileName(filePath)} -->\n";
            var headerSize = contextHeader.Length;
            var availableSpace = targetChunkSize - headerSize;
            
            // Build chunk content with HTML-aware breaking
            while (currentLine < lines.Length && currentSize < availableSpace)
            {
                var line = lines[currentLine];
                var lineSize = line.Length + 1; // +1 for newline
                
                // Check for good HTML breaking points when approaching limit
                if (currentSize + lineSize > availableSpace && chunkLines.Count > 0)
                {
                    if (IsGoodHtmlBreakingPoint(line) || currentSize > availableSpace * 0.8)
                    {
                        break;
                    }
                }
                
                chunkLines.Add(line);
                currentSize += lineSize;
                currentLine++;
            }
            
            // Create chunk if we have content
            if (chunkLines.Count > 0)
            {
                var chunkContent = contextHeader + string.Join("\n", chunkLines);
                var chunkStartLineNumber = startLine + chunkStartLine;
                var chunkEndLineNumber = startLine + currentLine - 1;
                
                chunks.Add(new CodeChunk(
                    $"{filePath}:{chunkStartLineNumber}:html-{chunkIndex}",
                    chunkContent.Trim(),
                    filePath,
                    chunkStartLineNumber,
                    chunkEndLineNumber,
                    "razor-html"
                ));
                
                chunkIndex++;
            }
            
            // Calculate overlap for next chunk
            if (currentLine < lines.Length)
            {
                var overlapLines = Math.Min((int)(chunkLines.Count * overlapPercentage), _slidingWindowConfig.MaxOverlapLines);
                currentLine = Math.Max(chunkStartLine + chunkLines.Count - overlapLines, chunkStartLine + 1);
            }
        }
        
        return chunks;
    }
    
    /// <summary>
    /// Determines good breaking points for HTML/Razor markup chunks
    /// </summary>
    private static bool IsGoodHtmlBreakingPoint(string line)
    {
        var trimmed = line.Trim();
        
        // Empty lines are always good breaking points
        if (trimmed.Length == 0)
            return true;
        
        // HTML comments
        if (trimmed.StartsWith("<!--") || trimmed.StartsWith("@*"))
            return true;
        
        // Block-level HTML elements (opening and closing tags)
        var blockElements = new[] {
            "<div", "</div>", "<section", "</section>", "<article", "</article>",
            "<header", "</header>", "<footer", "</footer>", "<main", "</main>",
            "<nav", "</nav>", "<aside", "</aside>", "<form", "</form>",
            "<table", "</table>", "<thead", "</thead>", "<tbody", "</tbody>",
            "<tr", "</tr>", "<fieldset", "</fieldset>", "<ul", "</ul>", "<ol", "</ol>",
            "<li", "</li>", "<h1", "<h2", "<h3", "<h4", "<h5", "<h6",
            "</h1>", "</h2>", "</h3>", "</h4>", "</h5>", "</h6>"
        };
        
        foreach (var element in blockElements)
        {
            if (trimmed.StartsWith(element, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        
        // Razor directives and control structures
        if (trimmed.StartsWith("@if") || trimmed.StartsWith("@else") ||
            trimmed.StartsWith("@for") || trimmed.StartsWith("@foreach") ||
            trimmed.StartsWith("@while") || trimmed.StartsWith("@switch") ||
            trimmed.StartsWith("@using") || trimmed.StartsWith("@{") ||
            trimmed == "}" || trimmed.StartsWith("@code") ||
            trimmed.StartsWith("@functions"))
        {
            return true;
        }
        
        // Blazor components (both opening and closing)
        if (trimmed.StartsWith("<") && (
            char.IsUpper(trimmed[1]) || // Component starts with capital letter
            trimmed.StartsWith("</") && trimmed.Length > 2 && char.IsUpper(trimmed[2])
        ))
        {
            return true;
        }
        
        return false;
    }
}