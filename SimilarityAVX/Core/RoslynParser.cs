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
    private const int MaxChunkSize = 2000; // Reasonable size for embeddings
    private readonly bool _includeFilePath;
    private readonly bool _includeProjectContext;
    
    public RoslynParser(bool includeFilePath = false, bool includeProjectContext = false)
    {
        _includeFilePath = includeFilePath;
        _includeProjectContext = includeProjectContext;
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
            
            // Extract namespace declarations
            foreach (var ns in root.DescendantNodes().OfType<NamespaceDeclarationSyntax>())
            {
                var nsContent = GetNamespaceSignature(ns);
                chunks.Add(CreateChunk(nsContent, ns, filePath, "namespace"));
            }
            
            // Extract file-scoped namespace declarations (.NET 6+)
            foreach (var ns in root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>())
            {
                var nsContent = $"namespace {ns.Name};";
                chunks.Add(CreateChunk(nsContent, ns, filePath, "namespace"));
            }
            
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
                var methodChunk = GetMethodWithContext(method);
                chunks.Add(CreateChunk(methodChunk, method, filePath, "method"));
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
    
    private string GetNamespaceSignature(NamespaceDeclarationSyntax ns)
    {
        var usings = ns.Usings.Select(u => u.ToString()).ToList();
        var content = string.Join("\n", usings);
        if (usings.Any()) content += "\n\n";
        content += $"namespace {ns.Name}";
        return content;
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
        foreach (var field in cls.Members.OfType<FieldDeclarationSyntax>().Take(10))
        {
            members.Add($"    {field.Modifiers} {field.Declaration.Type} {field.Declaration.Variables};");
        }
        
        // Add property signatures
        foreach (var prop in cls.Members.OfType<PropertyDeclarationSyntax>().Take(10))
        {
            var propSig = $"    {prop.Modifiers} {prop.Type} {prop.Identifier} {{ ";
            if (prop.AccessorList != null)
            {
                propSig += string.Join(" ", prop.AccessorList.Accessors.Select(a => a.Keyword.Text + ";"));
            }
            propSig += " }";
            members.Add(propSig);
        }
        
        // Add method signatures
        foreach (var method in cls.Members.OfType<MethodDeclarationSyntax>().Take(10))
        {
            var methodSig = $"    {method.Modifiers} {method.ReturnType} {method.Identifier}{method.ParameterList};";
            members.Add(methodSig);
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
        
        // Add XML documentation
        var xmlDocs = GetXmlDocumentation(method);
        if (!string.IsNullOrWhiteSpace(xmlDocs))
        {
            parts.Add(xmlDocs.Trim());
        }
        
        // Add the full method
        var methodStr = method.ToFullString().Trim();
        
        // If method is too large, truncate the body
        if (methodStr.Length > MaxChunkSize)
        {
            var signature = $"{method.Modifiers} {method.ReturnType} {method.Identifier}{method.ParameterList}";
            parts.Add(signature);
            parts.Add("{");
            parts.Add("    // Method body truncated for embedding...");
            parts.Add("}");
        }
        else
        {
            parts.Add(methodStr);
        }
        
        return string.Join("\n", parts);
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
        
        // Add XML documentation
        var xmlDocs = GetXmlDocumentation(prop);
        if (!string.IsNullOrWhiteSpace(xmlDocs))
        {
            parts.Add(xmlDocs.Trim());
        }
        
        // Add the property
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
        if (code.Length <= MaxChunkSize)
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
        
        // Configuration for sliding window
        var targetChunkSize = MaxChunkSize * 0.75; // Leave room for overlap and context
        
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
                
                // Add chunk metadata if multiple chunks
                if (lines.Length > MaxChunkSize / 50) // Only for larger files
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
            
            // Calculate overlap for next chunk
            if (currentLine < lines.Length)
            {
                var overlapLines = Math.Min((int)(chunkLines.Count * 0.15), 10); // Max 10 lines overlap
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
        if (fileName.EndsWith(".generated.cs") ||
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
            
            // 1. @using directives chunk
            if (usings.Any())
            {
                var usingContent = string.Join("\n", usings.Select(u => u.content));
                var startLine = usings.First().startLine;
                var endLine = usings.Last().endLine;
                chunks.Add(new CodeChunk(
                    $"{filePath}:{startLine}",
                    usingContent,
                    filePath,
                    startLine,
                    endLine,
                    "razor-using"
                ));
            }
            
            // 2. Parse @code sections as C# code
            int codeIndex = 1;
            foreach (var (codeSection, sectionStartLine, sectionEndLine) in codeSections)
            {
                // Parse the C# code inside @code blocks
                try
                {
                    var codeTree = CSharpSyntaxTree.ParseText(codeSection);
                    var codeRoot = codeTree.GetRoot();
                    
                    // Extract methods from code sections
                    foreach (var method in codeRoot.DescendantNodes().OfType<MethodDeclarationSyntax>())
                    {
                        var methodContent = GetRazorMethodWithContext(method, filePath);
                        chunks.Add(CreateRazorChunk(methodContent, method, filePath, "razor-method", codeIndex));
                        codeIndex++;
                    }
                    
                    // Extract properties from code sections
                    foreach (var prop in codeRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                    {
                        var propContent = GetRazorPropertyWithContext(prop, filePath);
                        chunks.Add(CreateRazorChunk(propContent, prop, filePath, "razor-property", codeIndex));
                        codeIndex++;
                    }
                    
                    // Extract fields from code sections
                    foreach (var field in codeRoot.DescendantNodes().OfType<FieldDeclarationSyntax>())
                    {
                        var fieldContent = $"// In {Path.GetFileName(filePath)}\n{field.ToString().Trim()}";
                        chunks.Add(CreateRazorChunk(fieldContent, field, filePath, "razor-field", codeIndex));
                        codeIndex++;
                    }
                    
                    // If no specific members found, add the whole @code block
                    if (!codeRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().Any() &&
                        !codeRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>().Any() &&
                        !codeRoot.DescendantNodes().OfType<FieldDeclarationSyntax>().Any())
                    {
                        var fullCodeContent = $"// Razor @code block in {Path.GetFileName(filePath)}\n{codeSection}";
                        chunks.Add(new CodeChunk(
                            $"{filePath}:{sectionStartLine}:code{codeIndex}",
                            fullCodeContent.Length > MaxChunkSize ? fullCodeContent.Substring(0, MaxChunkSize) + "\n// ... truncated" : fullCodeContent,
                            filePath,
                            sectionStartLine,
                            sectionEndLine,
                            "razor-code"
                        ));
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[RoslynParser] Error parsing @code section in {filePath}: {ex.Message}");
                    // Fallback: add as text chunk
                    var fallbackContent = $"// Razor @code block in {Path.GetFileName(filePath)}\n{codeSection}";
                    chunks.Add(new CodeChunk(
                        $"{filePath}:{sectionStartLine}:code{codeIndex}",
                        fallbackContent.Length > MaxChunkSize ? fallbackContent.Substring(0, MaxChunkSize) + "\n// ... truncated" : fallbackContent,
                        filePath,
                        sectionStartLine,
                        sectionEndLine,
                        "razor-code"
                    ));
                }
            }
            
            // 3. Create chunks for significant HTML/Razor sections
            foreach (var (htmlSection, startLine, endLine) in htmlSections.Where(h => h.content.Length > 100)) // Only significant sections
            {
                var htmlContent = $"<!-- Razor markup in {Path.GetFileName(filePath)} -->\n{htmlSection}";
                chunks.Add(new CodeChunk(
                    $"{filePath}:{startLine}",
                    htmlContent.Length > MaxChunkSize ? htmlContent.Substring(0, MaxChunkSize) + "\n<!-- ... truncated -->" : htmlContent,
                    filePath,
                    startLine,
                    endLine,
                    "razor-html"
                ));
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
        
        // Add any XML documentation
        var xmlDocs = GetXmlDocumentation(method);
        if (!string.IsNullOrWhiteSpace(xmlDocs))
        {
            parts.Add(xmlDocs.Trim());
        }
        
        // Add the method
        parts.Add(method.ToFullString().Trim());
        
        return string.Join("\n", parts);
    }
    
    private string GetRazorPropertyWithContext(PropertyDeclarationSyntax prop, string filePath)
    {
        var parts = new List<string>();
        
        parts.Add($"// Property in Razor component {Path.GetFileName(filePath)}");
        
        // Add any XML documentation
        var xmlDocs = GetXmlDocumentation(prop);
        if (!string.IsNullOrWhiteSpace(xmlDocs))
        {
            parts.Add(xmlDocs.Trim());
        }
        
        // Add the property
        parts.Add(prop.ToFullString().Trim());
        
        return string.Join("\n", parts);
    }
    
    private CodeChunk CreateRazorChunk(string content, SyntaxNode node, string filePath, string chunkType, int index, int sectionStartLine = 0)
    {
        var lineSpan = node.GetLocation().GetLineSpan();
        // Add the section's start line offset to get the actual file line numbers
        var startLine = lineSpan.StartLinePosition.Line + sectionStartLine;
        var endLine = lineSpan.EndLinePosition.Line + sectionStartLine;
        
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
}