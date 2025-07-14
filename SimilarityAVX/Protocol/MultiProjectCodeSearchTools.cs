using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using ModelContextProtocol.Server;
using CSharpMcpServer.Core;
using CSharpMcpServer.Models;
using CSharpMcpServer.Utils;
using System.Collections.Generic;

namespace CSharpMcpServer.Protocol;

/// <summary>
/// MCP tools for multi-project code search with WSL path conversion support.
/// Each project has its own isolated SQLite database and index.
/// 
/// USAGE GUIDELINES (Based on Real-World Testing):
/// 
/// SEMANTIC SEARCH EXCELS AT:
/// - Business logic exploration: "loan application processing", "contract management"
/// - UI component discovery: "validation form", "Blazor components"
/// - Conceptual searches: "anti-flood protection", "authentication middleware"
/// - Czech domain terms: Government/financial terminology performs better in Czech
/// 
/// USE TRADITIONAL TOOLS (grep/glob) FOR:
/// - Complete discovery: Finding ALL instances (100% coverage guarantee)
/// - Known exact patterns: regex, specific method signatures, file types
/// - Security audits: Require 100% completeness
/// - Cross-cutting concerns: Finding all touchpoints
/// 
/// SCORE INTERPRETATION (varies by embedding model):
/// Qwen3-8B: 0.45+ relevant, 0.70+ excellent
/// Voyage AI: 0.40+ relevant, 0.60+ excellent
/// Quick rule: Voyage AI scores ≈ Qwen3 scores - 0.15
/// 
/// MULTILINGUAL PERFORMANCE:
/// - Czech queries often outperform English for domain-specific terms
/// - Better results for government/financial terminology in Czech
/// - Use Czech for business processes, English for technical concepts
/// 
/// RECOMMENDED WORKFLOW:
/// 1. Start with semantic search for exploration
/// 2. Use context for understanding implementation  
/// 3. Apply filters for precise results
/// 4. Verify completeness with traditional tools when needed
/// </summary>
[McpServerToolType]
public static class MultiProjectCodeSearchTools
{
    // Cache indexers per project to avoid recreation
    private static readonly ConcurrentDictionary<string, CodeIndexer> _projectIndexers = new();
    
    private static CodeIndexer GetProjectIndexer(string projectName)
    {
        if (string.IsNullOrEmpty(projectName))
        {
            throw new ArgumentException("Project name is required for all operations");
        }
        
        return _projectIndexers.GetOrAdd(projectName, name =>
        {
            var config = Configuration.Load();
            config.Validate();
            
            Console.Error.WriteLine($"[MCP] Creating indexer for project '{name}': {config.Embedding.Model} @ {config.Embedding.ApiUrl}");
            return new CodeIndexer(config, name);
        });
    }
    
    [McpServerTool]
    [Description("Index a C# or C codebase for semantic search. Processes .cs, .razor, .cshtml, .c, .h files. Creates isolated project database. Supports incremental updates - only changed files are re-indexed. First indexing takes ~1 second per 100 files.")]
    public static async Task<string> IndexProject(
        [Description("Unique project identifier (e.g., 'frontend', 'backend-api', 'shared-lib')")] string project,
        [Description("Absolute path to the project root directory. WSL paths are automatically converted.")] string directory,
        [Description("Force complete re-indexing of all files. Use when switching models or after major changes.")] bool force = false)
    {
        try
        {
            // Convert WSL path to Windows path if needed
            var convertedDirectory = PathConverter.ConvertPath(directory);
            
            if (!Directory.Exists(convertedDirectory))
            {
                return $"Error: Directory not found: {convertedDirectory} (original: {directory})";
            }
            
            var indexer = GetProjectIndexer(project);
            
            // Create progress reporter
            var progress = new Progress<IndexProgress>(p =>
            {
                Console.Error.WriteLine($"[{project}] [{p.Phase}] {p.Current}/{p.Total} ({p.Percentage:F1}%)");
            });
            
            var stats = await indexer.IndexDirectoryAsync(convertedDirectory, force, progress);
            
            // Get memory stats after indexing
            var memStats = indexer.GetStats();
            
            // Get database size
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "csharp-mcp-server",
                $"codesearch-{project}.db"
            );
            var dbSizeMB = File.Exists(dbPath) ? new FileInfo(dbPath).Length / (1024.0 * 1024.0) : 0;
            
            // Build detailed response
            var response = new StringBuilder();
            response.AppendLine($"✅ Project '{project}' indexed successfully");
            response.AppendLine();
            response.AppendLine("📊 **Statistics:**");
            response.AppendLine($"- Total chunks: {memStats.ChunkCount:N0}");
            response.AppendLine($"- Unique files: {memStats.FileCount:N0}");
            response.AppendLine($"- Time taken: {stats.Duration.TotalSeconds:F1}s");
            response.AppendLine($"- Database size: {dbSizeMB:F1} MB");
            response.AppendLine($"- Memory usage: {memStats.MemoryUsageMB:F1} MB");
            response.AppendLine();
            
            if (force)
            {
                response.AppendLine("🔄 **Full reindex performed**");
                response.AppendLine($"- Files processed: {stats.FilesProcessed}");
                response.AppendLine($"- Chunks created: {stats.ChunksCreated}");
                response.AppendLine($"- Files skipped: {stats.FilesSkipped}");
            }
            else if (stats.Changes != null)
            {
                response.AppendLine("📝 **Changes detected:**");
                
                // Show specific file changes
                if (stats.Changes.Added.Any())
                {
                    response.AppendLine($"- **Added ({stats.Changes.Added.Count} files):**");
                    foreach (var file in stats.Changes.Added.Take(5))
                    {
                        var relativePath = GetRelativePath(file, convertedDirectory);
                        var timeAgo = GetTimeAgo(file);
                        response.AppendLine($"  + {relativePath} {timeAgo}");
                    }
                    if (stats.Changes.Added.Count > 5)
                    {
                        response.AppendLine($"  + ... and {stats.Changes.Added.Count - 5} more");
                    }
                    response.AppendLine();
                }
                
                if (stats.Changes.Modified.Any())
                {
                    response.AppendLine($"- **Modified ({stats.Changes.Modified.Count} files):**");
                    foreach (var file in stats.Changes.Modified.Take(5))
                    {
                        var relativePath = GetRelativePath(file, convertedDirectory);
                        var timeAgo = GetTimeAgo(file);
                        response.AppendLine($"  ~ {relativePath} {timeAgo}");
                    }
                    if (stats.Changes.Modified.Count > 5)
                    {
                        response.AppendLine($"  ~ ... and {stats.Changes.Modified.Count - 5} more");
                    }
                    response.AppendLine();
                }
                
                if (stats.Changes.Removed.Any())
                {
                    response.AppendLine($"- **Removed ({stats.Changes.Removed.Count} files):**");
                    foreach (var file in stats.Changes.Removed.Take(5))
                    {
                        var relativePath = GetRelativePath(file, convertedDirectory);
                        response.AppendLine($"  - {relativePath}");
                    }
                    if (stats.Changes.Removed.Count > 5)
                    {
                        response.AppendLine($"  - ... and {stats.Changes.Removed.Count - 5} more");
                    }
                    response.AppendLine();
                }
                
                // Calculate time saved
                var totalFiles = stats.Changes.Added.Count + stats.Changes.Modified.Count + stats.Changes.Removed.Count;
                var estimatedFullIndexTime = totalFiles * 0.5; // Estimate 0.5s per file
                var timeSaved = Math.Max(0, estimatedFullIndexTime - stats.Duration.TotalSeconds);
                
                response.AppendLine($"⏱️ **Performance:**");
                response.AppendLine($"- Incremental update: {stats.Duration.TotalSeconds:F1}s");
                response.AppendLine($"- Estimated full reindex: ~{estimatedFullIndexTime:F0}s");
                response.AppendLine($"- Time saved: ~{timeSaved:F0}s");
            }
            else
            {
                response.AppendLine("📝 **Index updated**");
                response.AppendLine($"- Files processed: {stats.FilesProcessed}");
                response.AppendLine($"- Chunks created: {stats.ChunksCreated}");
            }
            
            response.AppendLine();
            response.AppendLine($"💾 Index location: codesearch-{project}.db");
            response.AppendLine($"🚀 Search method: {memStats.SearchMethod}");
            
            return response.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error indexing project '{project}': {ex}");
            return $"Error: {ex.Message}";
        }
    }
    
    [McpServerTool]
    [Description("Search code using natural language queries. Returns relevance-scored results (0.0-1.0). Best for: conceptual searches ('email notifications'), UI components, business logic. Score thresholds vary by embedding model (Qwen3: 0.45+, Voyage: 0.40+). For finding ALL instances, use traditional grep/glob tools.")]
    public static async Task<string> SearchProject(
        [Description("Project name to search (must match name used in IndexProject)")] string project,
        [Description("Natural language query. Be specific for better results: 'email validation in registration' > 'validation'")] string query,
        [Description("Maximum results to return (1-20, default 5)")] int limit = 5)
    {
        try
        {
            var indexer = GetProjectIndexer(project);
            var results = await indexer.SearchAsync(query, limit);
            
            if (!results.Any())
            {
                var stats = indexer.GetStats();
                if (stats.ChunkCount == 0)
                {
                    return $"No results found in project '{project}' - Project appears to be empty or not indexed yet.\n\nTry running IndexProject first to index the codebase.";
                }
                return $"No results found in project '{project}' for query: {query}\n\nSuggestions:\n- Try broader terms (e.g., 'HTTP' instead of 'HttpClient')\n- Use different keywords (e.g., 'database' instead of 'SQL')\n- Check if the code you're looking for exists in this project\n- For finding ALL instances, use traditional tools (grep/glob) instead\n- Project contains {stats.ChunkCount} searchable code chunks";
            }
            
            // Convert paths back to WSL format if needed for display
            var formatted = string.Join("\n\n", results.Select((r, i) => 
            {
                // For display purposes, we keep the original Windows path
                // Claude can handle both formats
                var contentWithLineNumbers = r.Content;
                
                // Try to add line numbers if file exists
                if (File.Exists(r.FilePath))
                {
                    try
                    {
                        var lines = r.Content.Split('\n');
                        var numberedLines = lines.Select((line, idx) => 
                            $"{r.StartLine + idx,4}: {line}");
                        contentWithLineNumbers = string.Join("\n", numberedLines);
                    }
                    catch
                    {
                        // Fall back to content without line numbers
                    }
                }
                
                // Truncate long paths for display
                var displayPath = r.FilePath;
                if (displayPath.Length > 80)
                {
                    var fileName = Path.GetFileName(displayPath);
                    var dirName = Path.GetDirectoryName(displayPath) ?? "";
                    var dirs = dirName.Split(Path.DirectorySeparatorChar);
                    
                    // Show first dir (usually drive) + last 2 dirs + filename
                    if (dirs.Length > 3)
                    {
                        displayPath = $"{dirs[0]}{Path.DirectorySeparatorChar}...{Path.DirectorySeparatorChar}{string.Join(Path.DirectorySeparatorChar, dirs.TakeLast(2))}{Path.DirectorySeparatorChar}{fileName}";
                    }
                    else if (dirs.Length > 0)
                    {
                        // For shorter paths, just use ellipsis in the middle
                        var firstDir = dirs[0];
                        var lastDir = dirs[dirs.Length - 1];
                        displayPath = $"{firstDir}{Path.DirectorySeparatorChar}...{Path.DirectorySeparatorChar}{lastDir}{Path.DirectorySeparatorChar}{fileName}";
                    }
                }
                
                return $"""
                    **Result {i + 1}: {Path.GetFileName(r.FilePath)}:{r.StartLine}-{r.EndLine}** (score: {r.Score:F2}, type: {r.ChunkType})
                    File: {displayPath}
                    ```csharp
                    {contentWithLineNumbers}
                    ```
                    """;
            }));
            
            // Add score distribution hint if best score is low
            var scoreHint = "";
            if (results.Length > 0 && results[0].Score < 0.45)
            {
                scoreHint = $"\n\n💡 **Note:** Best score {results[0].Score:F2}. Score thresholds vary by model (Qwen3: 0.45+, Voyage: 0.40+).\n" +
                           "Try: broader search terms, check spelling, or use BatchSearch with related concepts.";
            }
            
            return $"Search results in project '{project}':\n\n{formatted}{scoreHint}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error searching project '{project}': {ex}");
            return $"Error: {ex.Message}";
        }
    }
    
    [McpServerTool]
    [Description("Delete all indexed data for a specific project. Removes database and frees memory. This operation is irreversible.")]
    public static string ClearProjectIndex(
        [Description("Project name to completely remove from the index")] string project)
    {
        try
        {
            var indexer = GetProjectIndexer(project);
            indexer.Clear();
            
            // Also remove from cache so next access creates fresh indexer
            _projectIndexers.TryRemove(project, out _);
            
            return $"Index for project '{project}' cleared successfully. Database removed.";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error clearing project '{project}': {ex}");
            return $"Error: {ex.Message}";
        }
    }
    
    [McpServerTool]
    [Description("Display index statistics: chunk count, memory usage (MB), vector dimensions, and CPU acceleration method (AVX-512/AVX2).")]
    public static string GetProjectStats(
        [Description("Project name to analyze")] string project)
    {
        try
        {
            var indexer = GetProjectIndexer(project);
            var stats = indexer.GetStats();
            var config = Configuration.Load();
            
            return $"""
                Index Statistics for project '{project}':
                - Total chunks: {stats.ChunkCount:N0}
                - Unique files: {stats.FileCount:N0}
                - Memory usage: {stats.MemoryUsageMB:F1} MB
                - Vector dimension: {stats.VectorDimension}
                - Precision: {stats.Precision}
                - Search method: {stats.SearchMethod}
                - Embedding model: {config.Embedding.Model}
                """;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error getting stats for project '{project}': {ex}");
            return $"Error: {ex.Message}";
        }
    }
    
    [McpServerTool]
    [Description("List all indexed projects with database sizes and last modification times.")]
    public static string ListProjects()
    {
        try
        {
            var config = Configuration.Load();
            var dbDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "csharp-mcp-server"
            );
            
            if (!Directory.Exists(dbDir))
            {
                return "No projects indexed yet. Database directory does not exist.";
            }
            
            var dbFiles = Directory.GetFiles(dbDir, "codesearch-*.db");
            if (dbFiles.Length == 0)
            {
                return "No projects indexed yet.";
            }
            
            var projects = dbFiles.Select(f =>
            {
                var fileName = Path.GetFileNameWithoutExtension(f);
                var projectName = fileName.Substring("codesearch-".Length);
                var fileInfo = new FileInfo(f);
                return $"- {projectName}: {fileInfo.Length / (1024.0 * 1024.0):F1} MB, modified {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm}";
            });
            
            return $"""
                Indexed projects:
                {string.Join("\n", projects)}
                
                Active in-memory projects: {_projectIndexers.Count}
                Current embedding model: {config.Embedding.Model}
                """;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error listing projects: {ex}");
            return $"Error: {ex.Message}";
        }
    }
    /*
    [McpServerTool]
    [Description("Test and debug WSL-to-Windows path conversion. Use this when IndexProject fails with 'Directory not found' errors to verify the path translation is working correctly.")]
    public static string TestPathConversion(
        [Description("WSL or Windows path to test conversion (e.g., '/mnt/e/Projects/MyApp' or 'C:\\Projects\\MyApp')")] string path)
    {
        try
        {
            var converted = PathConverter.ConvertPath(path);
            var isWindows = PathConverter.IsRunningOnWindows();
            
            return $"""
                Path conversion test:
                - Original path: {path}
                - Converted path: {converted}
                - Running on Windows: {isWindows}
                - Path changed: {path != converted}
                
                Note: Conversion only happens when running on Windows with WSL-style paths (/mnt/x/...)
                """;
        }
        catch (Exception ex)
        {
            return $"Error testing path: {ex.Message}";
        }
    }
    */
    [McpServerTool]
    [Description("Search with filters for precise results. Filter by file types and code structures. Common uses: async methods (chunkTypes='method'), auth code (chunkTypes='method-auth,class-auth'), Blazor components (fileTypes='razor'). Enhanced auth/security detection available. Use GetFilterHelp for complete list.")]
    public static async Task<string> SearchWithFilters(
        [Description("Project name to search")] string project,
        [Description("Natural language description of code you're looking for")] string query,
        [Description("File extensions to filter (comma-separated: 'cs', 'razor', 'cshtml', 'c', 'h'). Empty = all files.")] string? fileTypes = null,
        [Description("Code structures to filter (comma-separated). Basic: 'class', 'method', 'interface'. Enhanced: 'class-auth', 'method-auth'. Use GetFilterHelp for full list.")] string? chunkTypes = null,
        [Description("Maximum results to return (1-20, default 5)")] int limit = 5)
    {
        try
        {
            var indexer = GetProjectIndexer(project);
            
            // Get more results initially for filtering
            var initialLimit = Math.Max(limit * 3, 20);
            var allResults = await indexer.SearchAsync(query, initialLimit);
            
            if (!allResults.Any())
            {
                var stats = indexer.GetStats();
                if (stats.ChunkCount == 0)
                {
                    return $"No results found in project '{project}' - Project appears to be empty or not indexed yet.\n\nTry running IndexProject first to index the codebase.";
                }
                return $"No results found in project '{project}' for query: {query}\n\nTry broader search terms or check available filters.";
            }
            
            var filteredResults = allResults.AsEnumerable();
            
            // Apply file type filter
            if (!string.IsNullOrEmpty(fileTypes))
            {
                var extensions = fileTypes.Split(',')
                    .Select(ext => ext.Trim().ToLower())
                    .Where(ext => !string.IsNullOrEmpty(ext))
                    .ToHashSet();
                
                filteredResults = filteredResults.Where(r => 
                    extensions.Any(ext => r.FilePath.EndsWith($".{ext}", StringComparison.OrdinalIgnoreCase)));
            }
            
            // Apply chunk type filter
            if (!string.IsNullOrEmpty(chunkTypes))
            {
                var types = chunkTypes.Split(',')
                    .Select(t => t.Trim().ToLower())
                    .Where(t => !string.IsNullOrEmpty(t))
                    .ToHashSet();
                
#if DEBUG
                Console.Error.WriteLine($"[DEBUG] SearchWithFilters: Filtering for chunk types: {string.Join(", ", types)}");
                Console.Error.WriteLine($"[DEBUG] SearchWithFilters: Available chunk types in results: {string.Join(", ", allResults.Select(r => r.ChunkType).Distinct().Take(20))}");
#endif
                
                filteredResults = filteredResults.Where(r => 
                    types.Contains(r.ChunkType.ToLower()));
            }
            
            var finalResults = filteredResults.Take(limit).ToArray();
            
            if (!finalResults.Any())
            {
                var appliedFilters = new List<string>();
                if (!string.IsNullOrEmpty(fileTypes)) appliedFilters.Add($"file types: {fileTypes}");
                if (!string.IsNullOrEmpty(chunkTypes)) appliedFilters.Add($"chunk types: {chunkTypes}");
                
                return $"No results found in project '{project}' for query: {query}\nAfter applying filters: {string.Join(", ", appliedFilters)}\n\nSuggestions:\n- Try broader file types or chunk types\n- Remove some filters\n- Check if the code structure you're looking for exists in this project";
            }
            
            // Format results with filter information
            var filterInfo = new List<string>();
            if (!string.IsNullOrEmpty(fileTypes)) filterInfo.Add($"File types: {fileTypes}");
            if (!string.IsNullOrEmpty(chunkTypes)) filterInfo.Add($"Code types: {chunkTypes}");
            
            var header = filterInfo.Any() 
                ? $"Filtered search results in project '{project}' ({string.Join(", ", filterInfo)}):"
                : $"Search results in project '{project}':";
            
            var formatted = string.Join("\n\n", finalResults.Select((r, i) => 
            {
                var contentWithLineNumbers = r.Content;
                
                // Try to add line numbers if file exists
                if (File.Exists(r.FilePath))
                {
                    try
                    {
                        var lines = r.Content.Split('\n');
                        var numberedLines = lines.Select((line, idx) => 
                            $"{r.StartLine + idx,4}: {line}");
                        contentWithLineNumbers = string.Join("\n", numberedLines);
                    }
                    catch
                    {
                        // Fall back to content without line numbers
                    }
                }
                
                // Truncate long paths for display
                var displayPath = r.FilePath;
                if (displayPath.Length > 80)
                {
                    var fileName = Path.GetFileName(displayPath);
                    var dirName = Path.GetDirectoryName(displayPath) ?? "";
                    var dirs = dirName.Split(Path.DirectorySeparatorChar);
                    
                    // Show first dir (usually drive) + last 2 dirs + filename
                    if (dirs.Length > 3)
                    {
                        displayPath = $"{dirs[0]}{Path.DirectorySeparatorChar}...{Path.DirectorySeparatorChar}{string.Join(Path.DirectorySeparatorChar, dirs.TakeLast(2))}{Path.DirectorySeparatorChar}{fileName}";
                    }
                    else if (dirs.Length > 0)
                    {
                        // For shorter paths, just use ellipsis in the middle
                        var firstDir = dirs[0];
                        var lastDir = dirs[dirs.Length - 1];
                        displayPath = $"{firstDir}{Path.DirectorySeparatorChar}...{Path.DirectorySeparatorChar}{lastDir}{Path.DirectorySeparatorChar}{fileName}";
                    }
                }
                
                return $"""
                    **Result {i + 1}: {Path.GetFileName(r.FilePath)}:{r.StartLine}-{r.EndLine}** (score: {r.Score:F2}, type: {r.ChunkType})
                    File: {displayPath}
                    ```csharp
                    {contentWithLineNumbers}
                    ```
                    """;
            }));
            
            // Add score distribution hint if best score is low
            var scoreHint = "";
            if (finalResults.Length > 0 && finalResults[0].Score < 0.45)
            {
                scoreHint = $"\n\n💡 **Note:** Best score {finalResults[0].Score:F2}. Score thresholds vary by model (Qwen3: 0.45+, Voyage: 0.40+).\n" +
                           "Try: adjusting filters, broader search terms, or check GetFilterHelp for available options.";
            }
            
            return $"{header}\n\n{formatted}{scoreHint}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error in SearchWithFilters for project '{project}': {ex}");
            return $"Error: {ex.Message}";
        }
    }
    /*
    [McpServerTool]
    [Description("Export search results to JSON or Markdown format for sharing or further processing. Useful for documentation, reports, or integration with other tools.")]
    public static async Task<string> ExportSearchResults(
        [Description("Project name to search (must match exactly the name used in IndexProject)")] string project,
        [Description("Natural language description of code you're looking for")] string query,
        [Description("Export format: 'json' or 'markdown' (default: markdown)")] string format = "markdown",
        [Description("Maximum results to export (1-50, default: 20)")] int limit = 20)
    {
        try
        {
            // Validate format
            format = format.ToLowerInvariant();
            if (format != "json" && format != "markdown")
            {
                return "Error: Invalid format. Please use 'json' or 'markdown'.";
            }
            
            // Validate limit
            if (limit < 1 || limit > 50)
            {
                limit = Math.Max(1, Math.Min(50, limit));
            }
            
            // Get search results
            var indexer = GetProjectIndexer(project);
            var results = await indexer.SearchAsync(query, limit);
            
            if (!results.Any())
            {
                return format == "json" 
                    ? "{ \"project\": \"" + project + "\", \"query\": \"" + query + "\", \"results\": [] }"
                    : $"No results found in project '{project}' for query: {query}";
            }
            
            // Export based on format
            if (format == "json")
            {
                return ExportToJson(project, query, results);
            }
            else
            {
                return ExportToMarkdown(project, query, results);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error exporting results: {ex}");
            return $"Error: {ex.Message}";
        }
    }
    
    private static string ExportToJson(string project, string query, SearchResult[] results)
    {
        var exportData = new
        {
            project,
            query,
            timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            resultCount = results.Length,
            results = results.Select(r => new
            {
                file = r.FilePath,
                startLine = r.StartLine,
                endLine = r.EndLine,
                score = Math.Round(r.Score, 3),
                chunkType = r.ChunkType,
                content = r.Content
            }).ToArray()
        };
        
        return System.Text.Json.JsonSerializer.Serialize(exportData, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
    
    private static string ExportToMarkdown(string project, string query, SearchResult[] results)
    {
        var sb = new StringBuilder();
        
        // Header
        sb.AppendLine($"# C# Code Search Results");
        sb.AppendLine();
        sb.AppendLine($"**Project:** {project}  ");
        sb.AppendLine($"**Query:** {query}  ");
        sb.AppendLine($"**Timestamp:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"**Results:** {results.Length}  ");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        
        // Results
        for (int i = 0; i < results.Length; i++)
        {
            var r = results[i];
            var fileName = Path.GetFileName(r.FilePath);
            
            sb.AppendLine($"## Result {i + 1}: {fileName}");
            sb.AppendLine();
            sb.AppendLine($"**File:** `{r.FilePath}`  ");
            sb.AppendLine($"**Lines:** {r.StartLine}-{r.EndLine}  ");
            sb.AppendLine($"**Score:** {r.Score:F3}  ");
            sb.AppendLine($"**Type:** {r.ChunkType}  ");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(r.Content);
            sb.AppendLine("```");
            sb.AppendLine();
            
            if (i < results.Length - 1)
            {
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }
        
        // Footer
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"*Generated by C# MCP Server - Semantic Code Search*");
        
        return sb.ToString();
    }
    */
    /*
    [McpServerTool]
    [Description("Get a high-level architectural summary of a project including key classes, interfaces, controllers, and services. Includes security analysis showing authentication patterns, JWT/SAML/OAuth usage, and encryption components. NOTE: Enhanced authentication detection requires reindexing projects indexed before 2025-07-12 using force=true.")]
    public static async Task<string> GetProjectSummary(
        [Description("Project name to analyze (must be previously indexed)")] string project,
        [Description("Focus area: 'architecture' (default), 'controllers', 'services', 'security', 'all'")] string focus = "architecture")
    {
        try
        {
            var indexer = GetProjectIndexer(project);
            var stats = indexer.GetStats();
            
            if (stats.ChunkCount == 0)
            {
                return $"Project '{project}' has no indexed code. Please run IndexProject first.";
            }
            
            var sb = new StringBuilder();
            sb.AppendLine($"# Project Summary: {project}");
            sb.AppendLine();
            sb.AppendLine($"**Total Files:** {stats.FileCount:N0}  ");
            sb.AppendLine($"**Total Code Chunks:** {stats.ChunkCount:N0}  ");
            sb.AppendLine($"**Index Size:** {stats.MemoryUsageMB:F1} MB  ");
            sb.AppendLine();
            
            focus = focus.ToLowerInvariant();
            
            // Get key architectural elements based on focus
            if (focus == "all" || focus == "architecture")
            {
                sb.AppendLine("## 🏗️ Architecture Overview");
                sb.AppendLine();
                
                // Main classes
                var mainClasses = await indexer.SearchAsync("main class definition", 10);
                var classNames = ExtractTypeNames(mainClasses, "class");
                if (classNames.Any())
                {
                    sb.AppendLine("### Key Classes");
                    foreach (var className in classNames.Take(5))
                    {
                        sb.AppendLine($"- `{className}`");
                    }
                    sb.AppendLine();
                }
                
                // Interfaces
                var interfaces = await indexer.SearchAsync("interface definition public", 10);
                var interfaceNames = ExtractTypeNames(interfaces, "interface");
                if (interfaceNames.Any())
                {
                    sb.AppendLine("### Key Interfaces");
                    foreach (var iface in interfaceNames.Take(5))
                    {
                        sb.AppendLine($"- `{iface}`");
                    }
                    sb.AppendLine();
                }
            }
            
            if (focus == "all" || focus == "controllers" || focus == "architecture")
            {
                sb.AppendLine("## 🎮 Controllers");
                sb.AppendLine();
                
                var controllers = await indexer.SearchAsync("controller class API endpoint", 10);
                var controllerNames = ExtractTypeNames(controllers, "class", "controller");
                if (controllerNames.Any())
                {
                    foreach (var controller in controllerNames.Take(5))
                    {
                        sb.AppendLine($"- `{controller}`");
                    }
                }
                else
                {
                    sb.AppendLine("*No controllers found*");
                }
                sb.AppendLine();
            }
            
            if (focus == "all" || focus == "services" || focus == "architecture")
            {
                sb.AppendLine("## 🔧 Services");
                sb.AppendLine();
                
                var services = await indexer.SearchAsync("service class implementation business logic", 10);
                var serviceNames = ExtractTypeNames(services, "class", "service");
                if (serviceNames.Any())
                {
                    foreach (var service in serviceNames.Take(5))
                    {
                        sb.AppendLine($"- `{service}`");
                    }
                }
                else
                {
                    sb.AppendLine("*No services found*");
                }
                sb.AppendLine();
            }
            
            if (focus == "all" || focus == "security")
            {
                sb.AppendLine("## 🔐 Security & Authentication");
                sb.AppendLine();
                
                var authCode = await indexer.SearchAsync("authentication authorize login security", 10);
                var authTypes = ExtractTypeNames(authCode, null, "auth", "security", "login");
                if (authTypes.Any())
                {
                    sb.AppendLine("### Authentication Components");
                    foreach (var authType in authTypes.Take(5))
                    {
                        sb.AppendLine($"- `{authType}`");
                    }
                    sb.AppendLine();
                }
                
                // Look for specific auth patterns
                var authPatterns = new List<string>();
                if (authCode.Any(r => r.Content.Contains("JWT", StringComparison.OrdinalIgnoreCase)))
                    authPatterns.Add("JWT Authentication");
                if (authCode.Any(r => r.Content.Contains("SAML", StringComparison.OrdinalIgnoreCase)))
                    authPatterns.Add("SAML Authentication");
                if (authCode.Any(r => r.Content.Contains("OAuth", StringComparison.OrdinalIgnoreCase)))
                    authPatterns.Add("OAuth Authentication");
                if (authCode.Any(r => r.Content.Contains("[Authorize]", StringComparison.OrdinalIgnoreCase)))
                    authPatterns.Add("Attribute-based Authorization");
                
                if (authPatterns.Any())
                {
                    sb.AppendLine("### Detected Security Patterns");
                    foreach (var pattern in authPatterns)
                    {
                        sb.AppendLine($"- {pattern}");
                    }
                    sb.AppendLine();
                }
            }
            
            // Configuration detection
            var configFiles = await indexer.SearchAsync("configuration startup program.cs appsettings", 5);
            var configFileNames = configFiles
                .Select(r => Path.GetFileName(r.FilePath))
                .Distinct()
                .Where(f => f.Contains("config", StringComparison.OrdinalIgnoreCase) || 
                           f.Contains("program", StringComparison.OrdinalIgnoreCase) ||
                           f.Contains("startup", StringComparison.OrdinalIgnoreCase))
                .ToList();
                
            if (configFileNames.Any())
            {
                sb.AppendLine("## ⚙️ Configuration Files");
                foreach (var configFile in configFileNames.Take(5))
                {
                    sb.AppendLine($"- `{configFile}`");
                }
                sb.AppendLine();
            }
            
            sb.AppendLine("---");
            sb.AppendLine($"*Generated from {stats.ChunkCount:N0} indexed code chunks*");
            
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error generating project summary: {ex}");
            return $"Error: {ex.Message}";
        }
    }
    
    private static List<string> ExtractTypeNames(SearchResult[] results, string? typeFilter = null, params string[] nameFilters)
    {
        var typeNames = new HashSet<string>();
        
        foreach (var result in results)
        {
            // Filter by chunk type if specified
            if (typeFilter != null && !result.ChunkType.StartsWith(typeFilter))
                continue;
                
            var content = result.Content;
            var lines = content.Split('\n');
            
            foreach (var line in lines)
            {
                // Look for class/interface/record declarations
                if (line.Contains("class ") || line.Contains("interface ") || line.Contains("record "))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(line, 
                        @"(?:public\s+)?(?:abstract\s+)?(?:sealed\s+)?(?:partial\s+)?(?:class|interface|record)\s+(\w+)");
                    
                    if (match.Success)
                    {
                        var typeName = match.Groups[1].Value;
                        
                        // Apply name filters if any
                        if (nameFilters.Length == 0 || nameFilters.Any(f => typeName.ToLowerInvariant().Contains(f)))
                        {
                            typeNames.Add(typeName);
                        }
                    }
                }
            }
        }
        
        return typeNames.OrderBy(n => n).ToList();
    }
    */
    private static string GetRelativePath(string fullPath, string basePath)
    {
        try
        {
            var relative = Path.GetRelativePath(basePath, fullPath);
            return relative.Replace('\\', '/');
        }
        catch
        {
            return Path.GetFileName(fullPath);
        }
    }
    
    private static string GetTimeAgo(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var lastModified = File.GetLastWriteTimeUtc(filePath);
                var timeAgo = DateTime.UtcNow - lastModified;
                
                if (timeAgo.TotalMinutes < 1)
                    return "(just now)";
                if (timeAgo.TotalMinutes < 60)
                    return $"({(int)timeAgo.TotalMinutes} min ago)";
                if (timeAgo.TotalHours < 24)
                    return $"({(int)timeAgo.TotalHours} hours ago)";
                if (timeAgo.TotalDays < 7)
                    return $"({(int)timeAgo.TotalDays} days ago)";
                
                return "";
            }
        }
        catch { }
        
        return "";
    }
    
    private static int GetOptimalContextLines(string chunkType)
    {
        var type = chunkType.ToLowerInvariant();
        
        // Methods and functions often need more context
        if (type.Contains("method") || type.Contains("function"))
            return 18;
            
        // Classes benefit from seeing members
        if (type.Contains("class"))
            return 20;
            
        // Properties are usually concise
        if (type.Contains("property"))
            return 10;
            
        // Interfaces show contracts
        if (type.Contains("interface"))
            return 15;
            
        // Enums are typically short
        if (type.Contains("enum"))
            return 8;
            
        // Auth/security code benefits from extra context
        if (type.Contains("auth") || type.Contains("security"))
            return 20;
            
        // Default for unknown types
        return 15;
    }
    
    [McpServerTool]
    [Description("Search with extended context window (±1-20 lines). Shows line numbers for easy navigation. Essential for understanding complex business logic, authentication flows, or algorithm implementations. Default 15 lines provides good context.")]
    public static async Task<string> SearchWithContext(
        [Description("Project name to search")] string project,
        [Description("Natural language description of code you're looking for")] string query,
        [Description("Number of context lines before and after the match (1-20). Default: 15")] int contextLines = 15,
        [Description("Maximum results to return (1-10, default 5)")] int limit = 5)
    {
        try
        {
            // Validate parameters
            contextLines = Math.Max(1, Math.Min(20, contextLines));
            limit = Math.Max(1, Math.Min(10, limit));
            
            var indexer = GetProjectIndexer(project);
            var results = await indexer.SearchAsync(query, limit);
            
            if (!results.Any())
            {
                var stats = indexer.GetStats();
                if (stats.ChunkCount == 0)
                {
                    return $"No results found in project '{project}' - Project appears to be empty or not indexed yet.\n\nTry running IndexProject first.";
                }
                return $"No results found in project '{project}' for query: {query}";
            }
            
            var sb = new StringBuilder();
            sb.AppendLine($"## Search Results with Extended Context");
            sb.AppendLine($"**Project:** {project}  ");
            sb.AppendLine($"**Query:** {query}  ");
            sb.AppendLine($"**Context Lines:** ±{contextLines}  ");
            sb.AppendLine();
            
            for (int i = 0; i < results.Length; i++)
            {
                var result = results[i];
                var fileName = Path.GetFileName(result.FilePath);
                
                // Smart context adjustment based on chunk type
                var actualContextLines = contextLines;
                if (contextLines == 15) // Only apply smart adjustment if using default
                {
                    actualContextLines = GetOptimalContextLines(result.ChunkType);
                }
                
                sb.AppendLine($"### Result {i + 1}: {fileName}");
                sb.AppendLine($"**File:** `{result.FilePath}`  ");
                sb.AppendLine($"**Score:** {result.Score:F3}  ");
                sb.AppendLine($"**Type:** {result.ChunkType}  ");
                if (actualContextLines != contextLines)
                {
                    sb.AppendLine($"**Context:** ±{actualContextLines} lines (adjusted for {result.ChunkType})  ");
                }
                sb.AppendLine();
                
                // Try to read the file and show extended context
                if (File.Exists(result.FilePath))
                {
                    try
                    {
                        var allLines = File.ReadAllLines(result.FilePath);
                        var startContext = Math.Max(0, result.StartLine - actualContextLines - 1);
                        var endContext = Math.Min(allLines.Length, result.EndLine + actualContextLines);
                        
                        sb.AppendLine("```csharp");
                        
                        // Show context before
                        if (startContext < result.StartLine - 1)
                        {
                            for (int lineNum = startContext; lineNum < result.StartLine - 1; lineNum++)
                            {
                                sb.AppendLine($"{lineNum + 1,4}: {allLines[lineNum]}");
                            }
                            if (result.StartLine - startContext > actualContextLines + 1)
                            {
                                sb.AppendLine("      // ...");
                            }
                        }
                        
                        // Show the matched chunk with highlighting
                        sb.AppendLine($"      // === MATCH START (lines {result.StartLine}-{result.EndLine}) ===");
                        for (int lineNum = result.StartLine - 1; lineNum < Math.Min(result.EndLine, allLines.Length); lineNum++)
                        {
                            sb.AppendLine($"{lineNum + 1,4}: {allLines[lineNum]}");
                        }
                        sb.AppendLine($"      // === MATCH END ===");
                        
                        // Show context after
                        if (endContext > result.EndLine)
                        {
                            if (endContext - result.EndLine > actualContextLines + 1)
                            {
                                sb.AppendLine("      // ...");
                            }
                            for (int lineNum = result.EndLine; lineNum < endContext; lineNum++)
                            {
                                if (lineNum < allLines.Length)
                                {
                                    sb.AppendLine($"{lineNum + 1,4}: {allLines[lineNum]}");
                                }
                            }
                        }
                        
                        sb.AppendLine("```");
                    }
                    catch (Exception readEx)
                    {
                        // Fall back to showing just the chunk content
                        sb.AppendLine("```csharp");
                        sb.AppendLine(result.Content);
                        sb.AppendLine("```");
                        sb.AppendLine($"*Note: Could not read extended context: {readEx.Message}*");
                    }
                }
                else
                {
                    // File doesn't exist, show chunk content only
                    sb.AppendLine("```csharp");
                    sb.AppendLine(result.Content);
                    sb.AppendLine("```");
                    sb.AppendLine("*Note: Source file not accessible for extended context*");
                }
                
                sb.AppendLine();
                
                if (i < results.Length - 1)
                {
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
            }
            
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error in SearchWithContext: {ex}");
            return $"Error: {ex.Message}";
        }
    }
    /*
    [McpServerTool]
    [Description("Check index health: vector integrity, chunk statistics, and database consistency. Shows if authentication-aware chunks are present. Useful for diagnosing search issues, verifying enhanced features are active, or after system crashes. Use thorough=true for detailed chunk type analysis.")]
    public static async Task<string> CheckIndexHealth(
        [Description("Project name to check")] string project,
        [Description("Run thorough check including vector validation (slower but comprehensive)")] bool thorough = false)
    {
        try
        {
            var indexer = GetProjectIndexer(project);
            var stats = indexer.GetStats();
            
            if (stats.ChunkCount == 0)
            {
                return $"Project '{project}' has no indexed data. Run IndexProject first.";
            }
            
            var sb = new StringBuilder();
            sb.AppendLine($"# Index Health Report: {project}");
            sb.AppendLine();
            sb.AppendLine("## 📊 Basic Statistics");
            sb.AppendLine($"- Total chunks: {stats.ChunkCount:N0}");
            sb.AppendLine($"- Unique files: {stats.FileCount:N0}");
            sb.AppendLine($"- Memory usage: {stats.MemoryUsageMB:F1} MB");
            sb.AppendLine($"- Vector dimension: {stats.VectorDimension}");
            sb.AppendLine($"- Precision: {stats.Precision}");
            sb.AppendLine($"- Search method: {stats.SearchMethod}");
            sb.AppendLine();
            
            // Check database file
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "csharp-mcp-server",
                $"codesearch-{project}.db"
            );
            
            if (File.Exists(dbPath))
            {
                var dbInfo = new FileInfo(dbPath);
                sb.AppendLine("## 💾 Database Health");
                sb.AppendLine($"- Database size: {dbInfo.Length / (1024.0 * 1024.0):F1} MB");
                sb.AppendLine($"- Last modified: {dbInfo.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"- Status: ✅ Accessible");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("## 💾 Database Health");
                sb.AppendLine("- Status: ❌ Database file not found!");
                sb.AppendLine();
            }
            
            // Analyze chunk distribution
            sb.AppendLine("## 📈 Chunk Analysis");
            
            // Sample search to check responsiveness
            var testStart = DateTime.UtcNow;
            var testResults = await indexer.SearchAsync("class", 5);
            var searchTime = (DateTime.UtcNow - testStart).TotalMilliseconds;
            
            sb.AppendLine($"- Search responsiveness: {searchTime:F0}ms");
            sb.AppendLine($"- Test search results: {testResults.Length}");
            
            if (searchTime > 1000)
            {
                sb.AppendLine("  ⚠️ WARNING: Search is slow (>1s)");
            }
            else if (searchTime > 500)
            {
                sb.AppendLine("  ⚡ Search performance is acceptable");
            }
            else
            {
                sb.AppendLine("  🚀 Search performance is excellent");
            }
            sb.AppendLine();
            
            // Chunk type distribution
            if (thorough)
            {
                sb.AppendLine("## 🔍 Thorough Analysis");
                sb.AppendLine("*Analyzing chunk type distribution...*");
                
                // Get chunk type stats by sampling with a broad query
                var sampleResults = await indexer.SearchAsync("class method property interface namespace", 100); // Broad query for better sampling
                var chunkTypes = sampleResults
                    .GroupBy(r => r.ChunkType)
                    .Select(g => new { Type = g.Key, Count = g.Count() })
                    .OrderByDescending(x => x.Count)
                    .ToList();
                
                if (chunkTypes.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("### Chunk Type Distribution (sample):");
                    foreach (var type in chunkTypes)
                    {
                        var percentage = (type.Count / (double)sampleResults.Length) * 100;
                        sb.AppendLine($"- {type.Type}: {type.Count} ({percentage:F1}%)");
                    }
                }
                
                // Check for authentication chunks
                var authChunks = chunkTypes.Where(t => t.Type.Contains("-auth") || t.Type.Contains("-security")).ToList();
                if (authChunks.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("✅ Authentication-aware chunks detected:");
                    foreach (var auth in authChunks)
                    {
                        sb.AppendLine($"- {auth.Type}: {auth.Count}");
                    }
                }
                else
                {
                    sb.AppendLine();
                    sb.AppendLine("ℹ️ No authentication-specific chunks found");
                }
            }
            
            // Recommendations
            sb.AppendLine();
            sb.AppendLine("## 💡 Recommendations");
            
            var recommendations = new List<string>();
            
            if (stats.ChunkCount > 10000)
            {
                recommendations.Add("Large index detected. Consider using SearchWithFilters for better performance.");
            }
            
            if (stats.MemoryUsageMB > 100)
            {
                recommendations.Add("High memory usage. Ensure adequate system resources.");
            }
            
            if (searchTime > 500)
            {
                recommendations.Add("Search performance could be improved. Consider re-indexing with force=true.");
            }
            
            var avgChunksPerFile = stats.ChunkCount / (double)stats.FileCount;
            if (avgChunksPerFile > 20)
            {
                recommendations.Add($"High chunks per file ratio ({avgChunksPerFile:F1}). Some files may be too granularly chunked.");
            }
            
            if (recommendations.Any())
            {
                foreach (var rec in recommendations)
                {
                    sb.AppendLine($"- {rec}");
                }
            }
            else
            {
                sb.AppendLine("- ✅ Index appears healthy with no issues detected");
            }
            
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error checking index health: {ex}");
            return $"Error checking health: {ex.Message}";
        }
    }
    */
    [McpServerTool]
    [Description("Batch search multiple related queries efficiently. Ideal for exploring features or understanding implementations. Shows cross-references between results. More efficient than individual searches. Best with 3-5 related queries.")]
    public static async Task<string> BatchSearch(
        [Description("Project name to search")] string project,
        [Description("Comma-separated queries to search for. Example: 'login,authentication,session'")] string queries,
        [Description("Maximum results per query (1-5, default 3)")] int limitPerQuery = 3)
    {
        try
        {
            // Validate parameters
            limitPerQuery = Math.Max(1, Math.Min(5, limitPerQuery));
            
            // Parse queries
            var queryList = queries
                .Split(',')
                .Select(q => q.Trim())
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Distinct()
                .ToList();
            
            if (!queryList.Any())
            {
                return "Error: No valid queries provided. Please provide comma-separated search terms.";
            }
            
            if (queryList.Count > 10)
            {
                return "Error: Too many queries. Please limit to 10 queries per batch.";
            }
            
            var indexer = GetProjectIndexer(project);
            var stats = indexer.GetStats();
            
            if (stats.ChunkCount == 0)
            {
                return $"Project '{project}' has no indexed data. Run IndexProject first.";
            }
            
            var sb = new StringBuilder();
            sb.AppendLine($"# Batch Search Results: {project}");
            sb.AppendLine();
            sb.AppendLine($"**Queries:** {queryList.Count}  ");
            sb.AppendLine($"**Results per query:** {limitPerQuery}  ");
            sb.AppendLine();
            
            var totalResults = 0;
            var queryResults = new List<(string query, SearchResult[] results)>();
            
            // Execute all searches
            foreach (var query in queryList)
            {
                var results = await indexer.SearchAsync(query, limitPerQuery);
                queryResults.Add((query, results));
                totalResults += results.Length;
            }
            
            // Summary section
            sb.AppendLine("## 📊 Summary");
            sb.AppendLine($"- Total results found: {totalResults}");
            sb.AppendLine($"- Queries with results: {queryResults.Count(qr => qr.results.Any())}");
            sb.AppendLine($"- Empty queries: {queryResults.Count(qr => !qr.results.Any())}");
            sb.AppendLine();
            
            // Results by query
            sb.AppendLine("## 🔍 Results by Query");
            sb.AppendLine();
            
            foreach (var (query, results) in queryResults)
            {
                sb.AppendLine($"### Query: \"{query}\"");
                
                if (!results.Any())
                {
                    sb.AppendLine("*No results found*");
                }
                else
                {
                    sb.AppendLine($"**Found {results.Length} result{(results.Length > 1 ? "s" : "")}:**");
                    sb.AppendLine();
                    
                    foreach (var result in results)
                    {
                        var fileName = Path.GetFileName(result.FilePath);
                        sb.AppendLine($"- **{fileName}:{result.StartLine}** (score: {result.Score:F2}, type: {result.ChunkType})");
                        sb.AppendLine($"  `{result.FilePath}`");
                        
                        // Show first line of content as preview
                        var firstLine = result.Content.Split('\n').FirstOrDefault()?.Trim() ?? "";
                        if (firstLine.Length > 80)
                        {
                            firstLine = firstLine.Substring(0, 77) + "...";
                        }
                        if (!string.IsNullOrWhiteSpace(firstLine))
                        {
                            sb.AppendLine($"  > {firstLine}");
                        }
                        sb.AppendLine();
                    }
                }
                
                sb.AppendLine("---");
                sb.AppendLine();
            }
            
            // Cross-reference section for files that appear in multiple searches
            var fileOccurrences = queryResults
                .SelectMany(qr => qr.results.Select(r => new { qr.query, r.FilePath, r.Score }))
                .GroupBy(x => x.FilePath)
                .Where(g => g.Count() > 1)
                .Select(g => new { 
                    FilePath = g.Key, 
                    QueryScores = g.Select(x => new { x.query, x.Score })
                        .GroupBy(x => x.query)
                        .Select(qg => new { Query = qg.Key, Score = qg.Max(x => x.Score) })
                        .OrderByDescending(x => x.Score)
                        .ToList()
                })
                .OrderByDescending(x => x.QueryScores.Count)
                .ThenByDescending(x => x.QueryScores.Max(qs => qs.Score))
                .ToList();
            
            if (fileOccurrences.Any())
            {
                sb.AppendLine("## 🔗 Cross-Reference");
                sb.AppendLine("*Files appearing in multiple query results:*");
                sb.AppendLine();
                
                foreach (var file in fileOccurrences.Take(10))
                {
                    var fileName = Path.GetFileName(file.FilePath);
                    sb.AppendLine($"- **{fileName}** found in {file.QueryScores.Count} queries:");
                    foreach (var qs in file.QueryScores)
                    {
                        sb.AppendLine($"  - \"{qs.Query}\" (score: {qs.Score:F2})");
                    }
                }
                sb.AppendLine();
            }
            
            // Suggestions for empty queries
            var emptyQueries = queryResults.Where(qr => !qr.results.Any()).Select(qr => qr.query).ToList();
            if (emptyQueries.Any())
            {
                sb.AppendLine("## 💡 Suggestions for Empty Results");
                foreach (var emptyQuery in emptyQueries)
                {
                    sb.AppendLine($"- **\"{emptyQuery}\"**: Try simpler terms or check spelling");
                }
            }
            
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MCP] Error in batch search: {ex}");
            return $"Error: {ex.Message}";
        }
    }
    
    [McpServerTool]
    [Description("Show all available search filters, chunk types, file types, examples, and score interpretation. Essential reference for SearchWithFilters.")]
    public static string GetFilterHelp()
    {
        var sb = new StringBuilder();
        
        sb.AppendLine("# Search Filter Help");
        sb.AppendLine();
        sb.AppendLine("## 📁 File Type Filters");
        sb.AppendLine("Use the `fileTypes` parameter to filter by file extensions:");
        sb.AppendLine();
        sb.AppendLine("### C# and .NET Files");
        sb.AppendLine("- `cs` - C# source files (.cs)");
        sb.AppendLine("- `razor` - Blazor component files (.razor)");
        sb.AppendLine("- `cshtml` - Razor view files (.cshtml)");
        sb.AppendLine("- `csproj` - Project files (.csproj)");
        sb.AppendLine("- `json` - Configuration files (.json)");
        sb.AppendLine();
        sb.AppendLine("### C/C++ Files");
        sb.AppendLine("- `c` - C source files (.c)");
        sb.AppendLine("- `h` - C/C++ header files (.h)");
        sb.AppendLine();
        sb.AppendLine("**Example:** `fileTypes=\"cs,razor\"` - Search only in C# and Blazor files");
        sb.AppendLine();
        
        sb.AppendLine("## 🏗️ Chunk Type Filters");
        sb.AppendLine("Use the `chunkTypes` parameter to filter by code structure:");
        sb.AppendLine();
        sb.AppendLine("### C# Code Structures");
        sb.AppendLine("- `class` - Class definitions");
        sb.AppendLine("- `method` - Method implementations");
        sb.AppendLine("- `interface` - Interface definitions");
        sb.AppendLine("- `property` - Property definitions");
        sb.AppendLine("- `enum` - Enum definitions");
        sb.AppendLine();
        sb.AppendLine("### C Code Structures");
        sb.AppendLine("- `c-function` - C function implementations");
        sb.AppendLine("- `c-struct` - C struct definitions");
        sb.AppendLine("- `c-enum` - C enum definitions");
        sb.AppendLine("- `c-typedef` - C typedef declarations");
        sb.AppendLine("- `c-macro` - C macro definitions");
        sb.AppendLine();
        sb.AppendLine("### Blazor/Razor Structures");
        sb.AppendLine("- `razor-html` - Razor markup sections");
        sb.AppendLine("- `razor-method` - Methods in @code blocks");
        sb.AppendLine("- `razor-property` - Properties in @code blocks");
        sb.AppendLine("- `razor-code` - General @code blocks");
        sb.AppendLine();
        sb.AppendLine("### 🔒 Enhanced Security/Auth Types");
        sb.AppendLine("*Note: Requires reindexing with force=true if project was indexed before 2025-01-12*");
        sb.AppendLine();
        sb.AppendLine("- `class-auth` - Authentication-related classes (1.5x score boost)");
        sb.AppendLine("- `method-auth` - Authentication-related methods (1.5x score boost)");
        sb.AppendLine("- `method-security` - Security-related methods (1.4x score boost)");
        sb.AppendLine("- `c-function-auth` - C functions with auth patterns");
        sb.AppendLine("- `c-function-security` - C functions with security patterns");
        sb.AppendLine();
        sb.AppendLine("**Auth detection keywords:** login, auth, password, token, session, jwt, oauth, credential");
        sb.AppendLine("**Security detection keywords:** encrypt, decrypt, hash, validate, sanitize, verify");
        sb.AppendLine();
        
        sb.AppendLine("## 💡 Common Filter Combinations");
        sb.AppendLine();
        sb.AppendLine("### Find all authentication code:");
        sb.AppendLine("```");
        sb.AppendLine("chunkTypes=\"class-auth,method-auth\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Search only in Blazor components:");
        sb.AppendLine("```");
        sb.AppendLine("fileTypes=\"razor\"");
        sb.AppendLine("chunkTypes=\"razor-method,razor-property\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Find C security functions:");
        sb.AppendLine("```");
        sb.AppendLine("fileTypes=\"c,h\"");
        sb.AppendLine("chunkTypes=\"c-function-security,c-function-auth\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Search interfaces and classes:");
        sb.AppendLine("```");
        sb.AppendLine("chunkTypes=\"interface,class\"");
        sb.AppendLine("```");
        sb.AppendLine();
        
        sb.AppendLine("## 📊 Score Interpretation (varies by embedding model)");
        sb.AppendLine("**Qwen3-8B:** 0.45+ relevant, 0.70+ excellent");
        sb.AppendLine("**Voyage AI:** 0.40+ relevant, 0.60+ excellent");
        sb.AppendLine();
        sb.AppendLine("*Quick rule: Voyage AI scores ≈ Qwen3 scores - 0.15*");
        sb.AppendLine();
        
        sb.AppendLine("## 🔍 Usage Examples");
        sb.AppendLine();
        sb.AppendLine("### Find authentication middleware:");
        sb.AppendLine("```");
        sb.AppendLine("SearchWithFilters(project: \"myapp\", query: \"authentication middleware\", chunkTypes: \"class-auth,method-auth\")");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Search validation in Blazor forms:");
        sb.AppendLine("```");
        sb.AppendLine("SearchWithFilters(project: \"myapp\", query: \"form validation\", fileTypes: \"razor\", chunkTypes: \"razor-method\")");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Find all async methods:");
        sb.AppendLine("```");
        sb.AppendLine("SearchWithFilters(project: \"myapp\", query: \"async Task\", chunkTypes: \"method\")");
        sb.AppendLine("```");
        sb.AppendLine();
        
        sb.AppendLine("## ⚠️ Important Notes");
        sb.AppendLine("- Filters are case-insensitive");
        sb.AppendLine("- Multiple values are comma-separated");
        sb.AppendLine("- Empty filter = no filtering (all types included)");
        sb.AppendLine("- Enhanced auth types require reindexing for older projects");
        
        return sb.ToString();
    }
}