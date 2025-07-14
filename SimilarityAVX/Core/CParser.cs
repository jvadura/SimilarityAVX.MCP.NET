using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CppAst;
using CSharpMcpServer.Models;

namespace CSharpMcpServer.Core
{
    public class CParser
    {
        private readonly int _maxChunkSize;
        private readonly bool _includeFilePath;
        private readonly bool _includeProjectContext;

        public CParser(int maxChunkSize = 2000, bool includeFilePath = true, bool includeProjectContext = false)
        {
            _maxChunkSize = maxChunkSize;
            _includeFilePath = includeFilePath;
            _includeProjectContext = includeProjectContext;
        }

        public List<CodeChunk> ParseFile(string filePath)
        {
            var chunks = new List<CodeChunk>();
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            try
            {
                if (extension == ".h")
                {
                    // Use CppAst for header files
                    chunks = ParseHeaderFile(filePath);
                }
                else if (extension == ".c")
                {
                    // For .c files, use CppAst for declarations and fallback for implementations
                    chunks = ParseImplementationFile(filePath);
                }

                // Add file-level chunk if no chunks were created
                if (chunks.Count == 0)
                {
                    chunks.Add(CreateFileChunk(filePath));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing C file {filePath}: {ex.Message}");
                // Fallback to treating the whole file as a single chunk
                chunks.Add(CreateFileChunk(filePath));
            }

            return chunks;
        }

        private List<CodeChunk> ParseHeaderFile(string filePath)
        {
            var chunks = new List<CodeChunk>();
            
            try
            {
                var options = CreateParserOptions(filePath);
                var compilation = CppParser.ParseFile(filePath, options);
            
            if (compilation.HasErrors)
            {
                foreach (var diagnostic in compilation.Diagnostics.Messages)
                {
                    if (diagnostic.Type == CppLogMessageType.Error)
                    {
                        Console.WriteLine($"C parsing error in {filePath}: {diagnostic}");
                    }
                }
            }

            // Extract functions
            foreach (var function in compilation.Functions)
            {
                var chunk = CreateFunctionChunk(function, filePath);
                if (chunk != null)
                    chunks.Add(chunk);
            }

            // Extract structs
            foreach (var cppStruct in compilation.Classes) // CppAst stores structs as Classes
            {
                var chunk = CreateStructChunk(cppStruct, filePath);
                if (chunk != null)
                    chunks.Add(chunk);
            }

            // Extract enums
            foreach (var cppEnum in compilation.Enums)
            {
                var chunk = CreateEnumChunk(cppEnum, filePath);
                if (chunk != null)
                    chunks.Add(chunk);
            }

            // Extract typedefs
            foreach (var typedef in compilation.Typedefs)
            {
                var chunk = CreateTypedefChunk(typedef, filePath);
                if (chunk != null)
                    chunks.Add(chunk);
            }

            // Extract macros (if enabled)
            if (options.ParseMacros)
            {
                foreach (var macro in compilation.Macros)
                {
                    var chunk = CreateMacroChunk(macro, filePath);
                    if (chunk != null)
                        chunks.Add(chunk);
                }
            }
            }
            catch (DllNotFoundException)
            {
                Console.WriteLine($"Warning: CppAst requires libclang. Using text parsing for {Path.GetFileName(filePath)}");
                return ParseFileAsText(filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error using CppAst for {Path.GetFileName(filePath)}: {ex.Message}");
                return ParseFileAsText(filePath);
            }

            return chunks;
        }

        private List<CodeChunk> ParseImplementationFile(string filePath)
        {
            var chunks = new List<CodeChunk>();
            
            try
            {
                // Try to parse declarations with CppAst first
                var options = CreateParserOptions(filePath);
                var compilation = CppParser.ParseFile(filePath, options);
            
            // Extract function declarations
            foreach (var function in compilation.Functions)
            {
                var chunk = CreateFunctionChunk(function, filePath, isImplementation: true);
                if (chunk != null)
                    chunks.Add(chunk);
            }

            // For function implementations, we'll need to fall back to text-based parsing
            // since CppAst focuses on declarations
            var fileContent = File.ReadAllText(filePath);
            var implementationChunks = ExtractFunctionImplementations(fileContent, filePath);
            
                // Add implementation chunks
                chunks.AddRange(implementationChunks);
            }
            catch (DllNotFoundException)
            {
                Console.WriteLine($"Warning: CppAst requires libclang. Using text parsing for {Path.GetFileName(filePath)}");
                return ParseFileAsText(filePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error using CppAst for {Path.GetFileName(filePath)}: {ex.Message}");
                return ParseFileAsText(filePath);
            }

            return chunks;
        }

        private CppParserOptions CreateParserOptions(string filePath)
        {
            var options = new CppParserOptions
            {
                ParseMacros = true,
                ParseComments = true,
                AutoSquashTypedef = true
            };

            // Add the directory of the file as an include path
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                options.IncludeFolders.Add(directory);
                
                // Check for common include directories
                var includeDir = Path.Combine(directory, "include");
                if (Directory.Exists(includeDir))
                    options.IncludeFolders.Add(includeDir);
                
                var incDir = Path.Combine(directory, "inc");
                if (Directory.Exists(incDir))
                    options.IncludeFolders.Add(incDir);
            }

            // Configure for the current platform
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                options.ConfigureForWindowsMsvc(CppTargetCpu.X86_64);
            }
            else
            {
                // For Unix/Linux, manually set target
                options.TargetCpu = CppTargetCpu.X86_64;
                options.TargetSystem = "linux";
                // Add common Unix include paths
                options.SystemIncludeFolders.Add("/usr/include");
                options.SystemIncludeFolders.Add("/usr/local/include");
            }

            // Set C standard
            options.AdditionalArguments.Add("-std=c11");

            return options;
        }

        private CodeChunk? CreateFunctionChunk(CppFunction function, string filePath, bool isImplementation = false)
        {
            var sb = new StringBuilder();
            
            // Build function signature
            sb.Append($"{function.ReturnType} {function.Name}(");
            var parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Type} {p.Name}"));
            sb.Append(parameters);
            sb.Append(")");

            var chunkType = DetermineChunkType("c-function", function.Name);
            
            return new CodeChunk(
                $"{filePath}:{function.Span.Start.Line}",
                sb.ToString(),
                _includeFilePath ? filePath : Path.GetFileName(filePath),
                function.Span.Start.Line,
                function.Span.End.Line,
                chunkType);
        }

        private CodeChunk? CreateStructChunk(CppClass cppStruct, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"struct {cppStruct.Name} {{");
            
            foreach (var field in cppStruct.Fields)
            {
                sb.AppendLine($"    {field.Type} {field.Name};");
            }
            
            sb.Append("}");

            var chunkType = DetermineChunkType("c-struct", cppStruct.Name);
            
            return new CodeChunk(
                $"{filePath}:{cppStruct.Span.Start.Line}",
                sb.ToString(),
                _includeFilePath ? filePath : Path.GetFileName(filePath),
                cppStruct.Span.Start.Line,
                cppStruct.Span.End.Line,
                chunkType);
        }

        private CodeChunk? CreateEnumChunk(CppEnum cppEnum, string filePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"enum {cppEnum.Name} {{");
            
            foreach (var item in cppEnum.Items)
            {
                sb.AppendLine($"    {item.Name} = {item.Value},");
            }
            
            sb.Append("}");

            return new CodeChunk(
                $"{filePath}:{cppEnum.Span.Start.Line}",
                sb.ToString(),
                _includeFilePath ? filePath : Path.GetFileName(filePath),
                cppEnum.Span.Start.Line,
                cppEnum.Span.End.Line,
                "c-enum");
        }

        private CodeChunk? CreateTypedefChunk(CppTypedef typedef, string filePath)
        {
            var content = $"typedef {typedef.ElementType} {typedef.Name}";
            
            return new CodeChunk(
                $"{filePath}:{typedef.Span.Start.Line}",
                content,
                _includeFilePath ? filePath : Path.GetFileName(filePath),
                typedef.Span.Start.Line,
                typedef.Span.End.Line,
                "c-typedef");
        }

        private CodeChunk? CreateMacroChunk(CppMacro macro, string filePath)
        {
            var content = $"#define {macro.Name}";
            if (!string.IsNullOrEmpty(macro.Value))
            {
                content += $" {macro.Value}";
            }

            var chunkType = DetermineChunkType("c-macro", macro.Name);
            
            return new CodeChunk(
                $"{filePath}:{macro.Span.Start.Line}",
                content,
                _includeFilePath ? filePath : Path.GetFileName(filePath),
                macro.Span.Start.Line,
                macro.Span.End.Line,
                chunkType);
        }

        private List<CodeChunk> ExtractFunctionImplementations(string fileContent, string filePath)
        {
            var chunks = new List<CodeChunk>();
            var lines = fileContent.Split('\n');
            
            // Simple heuristic-based extraction for function implementations
            // This is a basic implementation - could be enhanced with tree-sitter later
            
            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i].Trim();
                
                // Look for function-like patterns (return type, name, parameters)
                if (IsFunctionSignature(line, lines, i))
                {
                    var functionChunk = ExtractFunctionBody(lines, i, filePath);
                    if (functionChunk != null)
                    {
                        chunks.Add(functionChunk);
                        i = functionChunk.EndLine;
                    }
                }
                
                i++;
            }
            
            return chunks;
        }

        private bool IsFunctionSignature(string line, string[] lines, int lineIndex)
        {
            // Skip preprocessor directives, comments, and empty lines
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith("//"))
                return false;
            
            // Look for opening brace on same line or next line
            if (line.Contains("{"))
                return line.Contains("(") && line.Contains(")");
            
            // Check next line for opening brace
            if (lineIndex + 1 < lines.Length)
            {
                var nextLine = lines[lineIndex + 1].Trim();
                if (nextLine.StartsWith("{"))
                    return line.Contains("(") && line.Contains(")");
            }
            
            return false;
        }

        private CodeChunk? ExtractFunctionBody(string[] lines, int startIndex, string filePath)
        {
            var functionSignature = lines[startIndex].Trim();
            var functionName = ExtractFunctionName(functionSignature);
            
            if (string.IsNullOrEmpty(functionName))
                return null;
            
            // Find the function body
            var sb = new StringBuilder();
            int braceCount = 0;
            int endIndex = startIndex;
            bool inBody = false;
            
            for (int i = startIndex; i < lines.Length; i++)
            {
                var line = lines[i];
                sb.AppendLine(line);
                
                foreach (char c in line)
                {
                    if (c == '{')
                    {
                        braceCount++;
                        inBody = true;
                    }
                    else if (c == '}')
                    {
                        braceCount--;
                    }
                }
                
                if (inBody && braceCount == 0)
                {
                    endIndex = i;
                    break;
                }
                
                // Limit chunk size
                if (sb.Length > _maxChunkSize)
                {
                    endIndex = i;
                    break;
                }
            }
            
            var chunkType = DetermineChunkType("c-function", functionName);
            
            return new CodeChunk(
                $"{filePath}:{startIndex + 1}",
                sb.ToString().Trim(),
                _includeFilePath ? filePath : Path.GetFileName(filePath),
                startIndex + 1, // Convert to 1-based
                endIndex + 1,
                chunkType);
        }

        private string ExtractFunctionName(string signature)
        {
            // Simple extraction - find the function name before the opening parenthesis
            var parenIndex = signature.IndexOf('(');
            if (parenIndex == -1)
                return string.Empty;
            
            var beforeParen = signature.Substring(0, parenIndex).Trim();
            var parts = beforeParen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            // The function name is typically the last part before the parenthesis
            return parts.Length > 0 ? parts[^1].TrimStart('*') : string.Empty;
        }

        private string DetermineChunkType(string baseType, string name)
        {
            var lowerName = name.ToLowerInvariant();
            
            // Check for authentication-related patterns
            if (lowerName.Contains("auth") || lowerName.Contains("login") || 
                lowerName.Contains("password") || lowerName.Contains("credential") ||
                lowerName.Contains("token") || lowerName.Contains("session"))
            {
                return baseType + "-auth";
            }
            
            // Check for security-related patterns
            if (lowerName.Contains("crypt") || lowerName.Contains("hash") || 
                lowerName.Contains("encrypt") || lowerName.Contains("decrypt") ||
                lowerName.Contains("secure") || lowerName.Contains("key"))
            {
                return baseType + "-security";
            }
            
            // Check for configuration patterns
            if (lowerName.Contains("config") || lowerName.Contains("setting") ||
                lowerName.Contains("option") || lowerName.Contains("param"))
            {
                return baseType + "-config";
            }
            
            return baseType;
        }

        private CodeChunk CreateFileChunk(string filePath)
        {
            try
            {
                var content = File.ReadAllText(filePath);
                var lines = content.Split('\n');
                
                return new CodeChunk(
                    $"{filePath}:1",
                    content.Length > _maxChunkSize 
                        ? content.Substring(0, _maxChunkSize) + "..." 
                        : content,
                    _includeFilePath ? filePath : Path.GetFileName(filePath),
                    1,
                    lines.Length,
                    "c-file");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading file {filePath}: {ex.Message}");
                return new CodeChunk(
                    $"{filePath}:1",
                    $"Error reading file: {ex.Message}",
                    _includeFilePath ? filePath : Path.GetFileName(filePath),
                    1,
                    1,
                    "c-file");
            }
        }
        
        private List<CodeChunk> ParseFileAsText(string filePath)
        {
            var chunks = new List<CodeChunk>();
            var fileContent = File.ReadAllText(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            if (extension == ".h")
            {
                // For header files, try to extract declarations using regex
                chunks.AddRange(ExtractHeaderDeclarations(fileContent, filePath));
            }
            else if (extension == ".c")
            {
                // For implementation files, extract function implementations
                chunks.AddRange(ExtractFunctionImplementations(fileContent, filePath));
            }
            
            // If no chunks were created, add the whole file as a chunk
            if (chunks.Count == 0)
            {
                chunks.Add(CreateFileChunk(filePath));
            }
            
            return chunks;
        }
        
        private List<CodeChunk> ExtractHeaderDeclarations(string content, string filePath)
        {
            var chunks = new List<CodeChunk>();
            var lines = content.Split('\n');
            
            // Simple pattern matching for common C declarations
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                
                // Skip empty lines and preprocessor directives (except important ones)
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#include"))
                    continue;
                
                // Look for function declarations
                if (line.Contains("(") && line.Contains(")") && line.Contains(";") && !line.StartsWith("//"))
                {
                    var functionName = ExtractFunctionName(line);
                    if (!string.IsNullOrEmpty(functionName))
                    {
                        var chunkType = DetermineChunkType("c-function", functionName);
                        chunks.Add(new CodeChunk(
                            $"{filePath}:{i + 1}",
                            line,
                            _includeFilePath ? filePath : Path.GetFileName(filePath),
                            i + 1,
                            i + 1,
                            chunkType));
                    }
                }
                // Look for struct definitions
                else if (line.StartsWith("typedef struct") || line.StartsWith("struct"))
                {
                    var structChunk = ExtractStructFromText(lines, i, filePath);
                    if (structChunk != null)
                    {
                        chunks.Add(structChunk);
                        i = structChunk.EndLine - 1; // Skip to end of struct
                    }
                }
                // Look for #define macros
                else if (line.StartsWith("#define"))
                {
                    var macroName = ExtractMacroName(line);
                    if (!string.IsNullOrEmpty(macroName))
                    {
                        var chunkType = DetermineChunkType("c-macro", macroName);
                        chunks.Add(new CodeChunk(
                            $"{filePath}:{i + 1}",
                            line,
                            _includeFilePath ? filePath : Path.GetFileName(filePath),
                            i + 1,
                            i + 1,
                            chunkType));
                    }
                }
            }
            
            return chunks;
        }
        
        private CodeChunk? ExtractStructFromText(string[] lines, int startIndex, string filePath)
        {
            var startLine = startIndex + 1;
            var endIndex = startIndex;
            var braceCount = 0;
            var inStruct = false;
            var sb = new StringBuilder();
            
            // Extract struct definition
            for (int i = startIndex; i < lines.Length; i++)
            {
                var line = lines[i];
                sb.AppendLine(line);
                
                if (line.Contains("{"))
                {
                    braceCount++;
                    inStruct = true;
                }
                
                if (line.Contains("}"))
                {
                    braceCount--;
                    if (inStruct && braceCount == 0)
                    {
                        endIndex = i;
                        // Check if there's a typedef name after the closing brace
                        if (i + 1 < lines.Length && lines[i + 1].Trim().EndsWith(";"))
                        {
                            sb.AppendLine(lines[i + 1]);
                            endIndex = i + 1;
                        }
                        break;
                    }
                }
            }
            
            if (endIndex > startIndex)
            {
                var structName = ExtractStructName(lines[startIndex]);
                var chunkType = DetermineChunkType("c-struct", structName);
                
                return new CodeChunk(
                    $"{filePath}:{startLine}",
                    sb.ToString().Trim(),
                    _includeFilePath ? filePath : Path.GetFileName(filePath),
                    startLine,
                    endIndex + 1,
                    chunkType);
            }
            
            return null;
        }
        
        private string ExtractStructName(string line)
        {
            // Handle "struct name {" or "typedef struct name {"
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "struct" && i + 1 < parts.Length)
                {
                    var name = parts[i + 1].TrimEnd('{').Trim();
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }
            }
            return "anonymous";
        }
        
        private string ExtractMacroName(string line)
        {
            // Extract macro name from #define NAME ...
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return parts[1].Split('(')[0]; // Handle function-like macros
            }
            return string.Empty;
        }
    }
}