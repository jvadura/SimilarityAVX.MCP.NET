using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CSharpMcpServer.Core;
using CSharpMcpServer.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// Check if running in console mode or MCP mode
if (args.Length > 0)
{
    // Console mode - existing functionality
    return await RunConsoleMode(args);
}
else
{
    // MCP server mode
    Console.Error.WriteLine("C# Semantic Code Search MCP Server starting...");
    Console.Error.WriteLine("This server enables semantic code search in C# projects.");
    
    // Validate configuration before starting server
    try
    {
        var config = Configuration.Load();
        config.Validate();
        Console.Error.WriteLine($"Configuration loaded successfully:");
        Console.Error.WriteLine($"  API URL: {config.Embedding.ApiUrl}");
        Console.Error.WriteLine($"  Model: {config.Embedding.Model}");
        Console.Error.WriteLine($"  Dimension: {config.Embedding.Dimension}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("\nERROR: Configuration validation failed!");
        Console.Error.WriteLine($"  {ex.Message}");
        Console.Error.WriteLine("\nPlease ensure:");
        Console.Error.WriteLine("  1. config.json exists in the application directory");
        Console.Error.WriteLine("  2. Environment variables are set (see below)");
        Console.Error.WriteLine("  3. All required settings are provided");
        Console.Error.WriteLine("\nRequired environment variables:");
        Console.Error.WriteLine("  EMBEDDING_API_KEY      (required) API key for embedding service");
        Console.Error.WriteLine("  EMBEDDING_API_URL      Base URL for API (e.g., http://10.0.0.186:11434)");
        Console.Error.WriteLine("  EMBEDDING_MODEL        Model name (e.g., snowflake-arctic-embed2:latest)");
        Console.Error.WriteLine("  EMBEDDING_DIMENSION    Vector dimension (e.g., 1024)");
        Console.Error.WriteLine("\nExiting due to configuration error.");
        return 1;
    }
    
    Console.Error.WriteLine("Server will listen on http://*:5001/sse");
    Console.Error.WriteLine("Add to Claude Desktop with: claude mcp add csharp-search --transport sse http://YOUR_IP:5001/sse");
    
    var builder = WebApplication.CreateBuilder(args);
    
    // Configure to listen on port 5001
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.ListenAnyIP(5001);
    });
    
    // Configure logging to stderr for MCP
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Information;
    });
    
    // Add configuration as singleton
    builder.Services.AddSingleton(Configuration.Load());
    
    // Add ProjectMonitor as a hosted service for automatic monitoring
    builder.Services.AddHostedService<ProjectMonitorService>();
    
    // Add MemoryTools as singleton for disposal
    builder.Services.AddSingleton<CSharpMcpServer.Protocol.MemoryTools>();
    
    // Add MCP server with tools
    builder.Services.AddMcpServer()
        .WithHttpTransport()  // HTTP transport with SSE for cross-machine access
        .WithToolsFromAssembly(); // Auto-discover tools
    
    var app = builder.Build();
    app.MapMcp();
    
    // Register shutdown handler to dispose resources
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        Console.Error.WriteLine("[MCP] Application stopping, disposing resources...");
        CSharpMcpServer.Protocol.MultiProjectCodeSearchTools.DisposeAllIndexers();
        
        // Dispose memory tools if they were created
        var memoryTools = app.Services.GetService<CSharpMcpServer.Protocol.MemoryTools>();
        memoryTools?.Dispose();
    });
    
    await app.RunAsync("http://*:5001");
    return 0;
}

async Task<int> RunConsoleMode(string[] args)
{
    Console.WriteLine("C# Semantic Code Search - Stage 1 Console");
    Console.WriteLine("==========================================");

    try
    {
        // Load configuration from JSON file or environment variables
        var config = Configuration.Load();
        config.Validate();
        
        var baseUrl = config.Embedding.ApiUrl;
        var apiKey = config.Embedding.ApiKey;
        var model = config.Embedding.Model;
        var dimension = config.Embedding.Dimension;
        var precision = config.Embedding.Precision;

        Console.WriteLine($"Configuration:");
        Console.WriteLine($"  API URL: {baseUrl}");
        Console.WriteLine($"  Model: {model}");
        Console.WriteLine($"  Dimension: {dimension}");
        Console.WriteLine($"  Precision: {precision}");
        Console.WriteLine();

        var command = args[0].ToLower();

        // Create indexer (no project name for console mode)
        using var indexer = new CodeIndexer(config, null);
        
        // Progress reporting
        var progress = new Progress<IndexProgress>(p =>
        {
            Console.Write($"\r[{p.Phase}] {p.Current}/{p.Total} ({p.Percentage:F1}%)          ");
        });
        
        switch (command)
        {
            case "index":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("ERROR: Please specify a directory to index");
                    Console.Error.WriteLine("Usage: dotnet run -- index \"C:\\MyProject\" [--force]");
                    return 1;
                }
                
                var directory = args[1];
                var force = args.Length > 2 && args[2] == "--force";
                
                Console.WriteLine($"Indexing directory: {directory}");
                if (force) Console.WriteLine("Force reindex: enabled");
                
                var stopwatch = Stopwatch.StartNew();
                var stats = await indexer.IndexDirectoryAsync(directory, force, progress);
                stopwatch.Stop();
                
                Console.WriteLine(); // New line after progress
                Console.WriteLine($"\nIndexing complete!");
                Console.WriteLine($"  Files processed: {stats.FilesProcessed}");
                Console.WriteLine($"  Chunks created: {stats.ChunksCreated}");
                Console.WriteLine($"  Files skipped: {stats.FilesSkipped}");
                Console.WriteLine($"  Total time: {stats.Duration.TotalSeconds:F1}s");
                Console.WriteLine($"  Speed: {stats.FilesProcessed / stats.Duration.TotalSeconds:F1} files/sec");
                
                if (stats.ChunksCreated > 0)
                {
                    Console.WriteLine($"\nAll vectors loaded to memory for SIMD-accelerated search.");
                    
                    // Show memory usage
                    var indexStats = indexer.GetStats();
                    Console.WriteLine($"Memory usage: {indexStats.MemoryUsageMB:F1}MB ({indexStats.ChunkCount} chunks)");
                    Console.WriteLine($"Search method: {indexStats.SearchMethod}");
                }
                break;
                
            case "search":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("ERROR: Please specify a search query");
                    Console.Error.WriteLine("Usage: dotnet run -- search \"your search query\" [limit]");
                    return 1;
                }
                
                var query = args[1];
                var limit = args.Length > 2 && int.TryParse(args[2], out var l) ? l : 5;
                
                Console.WriteLine($"Searching for: \"{query}\"");
                Console.WriteLine($"Limit: {limit} results\n");
                
                var searchStopwatch = Stopwatch.StartNew();
                var results = await indexer.SearchAsync(query, limit);
                searchStopwatch.Stop();
                
                if (!results.Any())
                {
                    Console.WriteLine("No results found.");
                }
                else
                {
                    Console.WriteLine($"Found {results.Length} results in {searchStopwatch.ElapsedMilliseconds}ms:\n");
                    
                    for (int i = 0; i < results.Length; i++)
                    {
                        var result = results[i];
                        Console.WriteLine($"--- Result {i + 1} ---");
                        Console.WriteLine($"File: {TruncatePath(result.FilePath)}:{result.StartLine}-{result.EndLine}");
                        Console.WriteLine($"Score: {result.Score:F3}");
                        Console.WriteLine($"Type: {result.ChunkType}");
                        Console.WriteLine($"Content:\n{result.Content}");
                        Console.WriteLine();
                    }
                }
                
                // Show search performance
                var searchStats = indexer.GetStats();
                Console.WriteLine($"\nSearch performance:");
                Console.WriteLine($"  Search time: {searchStopwatch.ElapsedMilliseconds}ms");
                Console.WriteLine($"  Index size: {searchStats.ChunkCount} chunks");
                Console.WriteLine($"  Search method: {searchStats.SearchMethod}");
                break;
                
            case "stats":
                Console.WriteLine("Index Statistics:");
                Console.WriteLine("=================");
                
                var currentStats = indexer.GetStats();
                Console.WriteLine($"Total chunks: {currentStats.ChunkCount:N0}");
                Console.WriteLine($"Total files: {currentStats.FileCount:N0}");
                Console.WriteLine($"Memory usage: {currentStats.MemoryUsageMB:F1} MB");
                Console.WriteLine($"Vector dimension: {currentStats.VectorDimension}");
                Console.WriteLine($"Vector precision: {currentStats.Precision}");
                Console.WriteLine($"Search method: {currentStats.SearchMethod}");
                
                if (currentStats.ChunkCount > 0)
                {
                    var bytesPerChunk = currentStats.Precision == VectorPrecision.Half 
                        ? currentStats.VectorDimension * 2 
                        : currentStats.VectorDimension * 4;
                    var vectorMemoryMB = (currentStats.ChunkCount * bytesPerChunk) / (1024.0 * 1024.0);
                    Console.WriteLine($"\nMemory breakdown:");
                    Console.WriteLine($"  Vectors: {vectorMemoryMB:F1} MB");
                    Console.WriteLine($"  Metadata: {currentStats.MemoryUsageMB - vectorMemoryMB:F1} MB");
                }
                break;
                
            case "clear":
                Console.WriteLine("Clearing index...");
                indexer.Clear();
                Console.WriteLine("Index cleared successfully. All data removed from memory and disk.");
                break;
                
            // case "test-cache":
            //     Console.WriteLine("Running SQLite cache tests...\n");
            //     await CSharpMcpServer.Tests.TestSqliteCache.RunAllTests();
            //     break;
                
            // case "test-topk":
            //     Console.WriteLine("Running TopK performance benchmark...\n");
            //     CSharpMcpServer.Tests.TestTopKPerformance.RunBenchmark();
            //     break;
                
            case "bench":
                // Parse benchmark parameters
                int dim = 2048, count = 10000, iter = 100, searches = 10;
                
                if (args.Length > 1 && !int.TryParse(args[1], out dim))
                {
                    Console.Error.WriteLine($"Invalid dimension: {args[1]}");
                    return 1;
                }
                if (args.Length > 2 && !int.TryParse(args[2], out count))
                {
                    Console.Error.WriteLine($"Invalid vector count: {args[2]}");
                    return 1;
                }
                if (args.Length > 3 && !int.TryParse(args[3], out iter))
                {
                    Console.Error.WriteLine($"Invalid iterations: {args[3]}");
                    return 1;
                }
                if (args.Length > 4 && !int.TryParse(args[4], out searches))
                {
                    Console.Error.WriteLine($"Invalid search count: {args[4]}");
                    return 1;
                }
                
                CSharpMcpServer.Utils.BenchmarkRunner.RunBenchmark(dim, count, iter, searches);
                break;
            
            // Memory commands
            case "memory-add":
                if (args.Length < 4)
                {
                    Console.Error.WriteLine("Usage: memory-add <project> <name> <content> [tags]");
                    return 1;
                }
                // Replace literal \n with actual newlines
var content = args[3].Replace("\\n", "\n");
await RunMemoryAdd(args[1], args[2], content, args.Length > 4 ? args[4] : null);
                break;
                
            case "memory-add-file":
                if (args.Length < 4)
                {
                    Console.Error.WriteLine("Usage: memory-add-file <project> <name> <file_path> [tags] [parentId]");
                    return 1;
                }
                var tags = args.Length > 4 ? args[4] : null;
                var parentIdStr = args.Length > 5 ? args[5] : null;
                await RunMemoryAddFile(args[1], args[2], args[3], tags, parentIdStr);
                break;
                
            case "memory-append":
                if (args.Length < 5)
                {
                    Console.Error.WriteLine("Usage: memory-append <project> <parentIdOrAlias> <name> <content> [tags]");
                    return 1;
                }
                var appendContent = args[4].Replace("\\n", "\n");
                await RunMemoryAppend(args[1], args[2], args[3], appendContent, args.Length > 5 ? args[5] : null);
                break;
                
            case "memory-search":
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: memory-search <project> <query> [topK]");
                    return 1;
                }
                var topK = args.Length > 3 ? int.Parse(args[3]) : 3;
                await RunMemorySearch(args[1], args[2], topK);
                break;
                
            case "memory-get":
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: memory-get <project> <memoryIdOrAlias>");
                    return 1;
                }
                await RunMemoryGet(args[1], args[2]);
                break;
                
            case "memory-list":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: memory-list <project> [tagFilter]");
                    return 1;
                }
                await RunMemoryList(args[1], args.Length > 2 ? args[2] : null);
                break;
                
            case "memory-delete":
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: memory-delete <project> <memoryIdOrAlias>");
                    return 1;
                }
                await RunMemoryDelete(args[1], args[2]);
                break;
                
            case "memory-stats":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: memory-stats <project>");
                    return 1;
                }
                await RunMemoryStats(args[1]);
                break;
                
            case "memory-import-markdown":
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: memory-import-markdown <project> <markdownFile> [tags] [parentMemoryId]");
                    return 1;
                }
                var importTags = args.Length > 3 ? args[3] : null;
                var importParentIdStr = args.Length > 4 ? args[4] : null;
                await RunMemoryImportMarkdown(args[1], args[2], importTags, importParentIdStr);
                break;
                
            case "memory-tree":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: memory-tree <project> [rootMemoryIdOrAlias] [maxDepth] [includeContent]");
                    return 1;
                }
                var treeRootIdOrAlias = args.Length > 2 ? args[2] : null;
                var treeMaxDepth = args.Length > 3 ? int.Parse(args[3]) : 5;
                var treeIncludeContent = args.Length > 4 ? bool.Parse(args[4]) : false;
                await RunMemoryTree(args[1], treeRootIdOrAlias, treeMaxDepth, treeIncludeContent);
                break;
                
            case "memory-export":
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Usage: memory-export <project> [rootMemoryIdOrAlias] [format] [outputFile]");
                    Console.Error.WriteLine("Formats: markdown (default), json, yaml");
                    return 1;
                }
                var exportRootIdOrAlias = args.Length > 2 ? args[2] : null;
                var exportFormat = args.Length > 3 ? args[3] : "markdown";
                var exportOutputFile = args.Length > 4 ? args[4] : null;
                await RunMemoryExport(args[1], exportRootIdOrAlias, exportFormat, exportOutputFile);
                break;
                
            case "memory-update":
                if (args.Length < 3)
                {
                    Console.Error.WriteLine("Usage: memory-update <project> <memoryIdOrAlias> [--name \"new name\"] [--content \"new content\"] [--tags \"tag1,tag2\"]");
                    return 1;
                }
                await RunMemoryUpdate(args[1], args[2], args.Skip(3).ToArray());
                break;
                
            default:
                Console.Error.WriteLine($"Unknown command: {command}");
                ShowUsage();
                return 1;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\nERROR: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
        }
        return 1;
    }

    return 0;
}

string TruncatePath(string filePath, int maxLength = 80)
{
    if (filePath.Length <= maxLength)
        return filePath;
        
    var fileName = Path.GetFileName(filePath);
    var dirName = Path.GetDirectoryName(filePath) ?? "";
    var dirs = dirName.Split(Path.DirectorySeparatorChar);
    
    // Show first dir (usually drive) + last 2 dirs + filename
    if (dirs.Length > 3)
    {
        return $"{dirs[0]}{Path.DirectorySeparatorChar}...{Path.DirectorySeparatorChar}{string.Join(Path.DirectorySeparatorChar, dirs.TakeLast(2))}{Path.DirectorySeparatorChar}{fileName}";
    }
    else if (dirs.Length > 0)
    {
        // For shorter paths, just use ellipsis in the middle
        var firstDir = dirs[0];
        var lastDir = dirs[dirs.Length - 1];
        return $"{firstDir}{Path.DirectorySeparatorChar}...{Path.DirectorySeparatorChar}{lastDir}{Path.DirectorySeparatorChar}{fileName}";
    }
    
    return filePath;
}

void ShowUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- index \"C:\\MyProject\" [--force]   # Index a directory");
    Console.WriteLine("  dotnet run -- search \"query\" [limit]           # Search indexed code");
    Console.WriteLine("  dotnet run -- stats                             # Show index statistics");
    Console.WriteLine("  dotnet run -- clear                             # Clear all indexed data");
    Console.WriteLine("  dotnet run -- bench [dim] [vectors] [iter] [searches]  # Run performance benchmark");
    Console.WriteLine("                                                 # Default: 2048 10000 100 10");
    Console.WriteLine();
    Console.WriteLine("Memory commands:");
    Console.WriteLine("  dotnet run -- memory-add <project> <name> <content> [tags]            # Add a memory");
    Console.WriteLine("  dotnet run -- memory-add-file <project> <name> <file_path> [tags] [parentId]  # Add memory from file");
    Console.WriteLine("  dotnet run -- memory-append <project> <parentIdOrAlias> <name> <content> [tags]  # Append child memory");
    Console.WriteLine("  dotnet run -- memory-search <project> <query> [topK]                        # Search memories");
    Console.WriteLine("  dotnet run -- memory-get <project> <memoryIdOrAlias>                        # Get a memory");
    Console.WriteLine("  dotnet run -- memory-list <project> [tagFilter]                             # List memories");
    Console.WriteLine("  dotnet run -- memory-delete <project> <memoryIdOrAlias>                     # Delete a memory");
    Console.WriteLine("  dotnet run -- memory-stats <project>                                  # Memory statistics");
    Console.WriteLine("  dotnet run -- memory-import-markdown <project> <markdownFile> [tags] [parentId]  # Import markdown hierarchy");
    Console.WriteLine("  dotnet run -- memory-tree <project> [rootIdOrAlias] [maxDepth] [includeContent]  # Display memory tree");
    Console.WriteLine("  dotnet run -- memory-export <project> [rootIdOrAlias] [format] [outputFile]       # Export memories");
    Console.WriteLine("  dotnet run -- memory-update <project> <memoryIdOrAlias> [--name \"...\"] [--content \"...\"] [--tags \"...\"]  # Update memory");
    Console.WriteLine();
    Console.WriteLine("Environment variables:");
    Console.WriteLine("  EMBEDDING_API_KEY      (required) API key for embedding service");
    Console.WriteLine("  EMBEDDING_DIMENSION    (required) Vector dimension (e.g., 1024, 1536, 4096)");
    Console.WriteLine("  EMBEDDING_API_URL      Base URL for API (default: https://api.openai.com)");
    Console.WriteLine("  EMBEDDING_MODEL        Model name (default: text-embedding-3-small)");
    Console.WriteLine("  EMBEDDING_PRECISION    float32 or half (default: float32)");
    Console.WriteLine("  EMBEDDING_BATCH_SIZE   Batch size for embeddings (default: 50)");
}

// Memory command implementations
async Task RunMemoryAdd(string project, string name, string content, string? tags)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    var memory = new CSharpMcpServer.Models.Memory
    {
        ProjectName = project,
        MemoryName = name,
        FullDocumentText = content,
        Tags = string.IsNullOrEmpty(tags) ? new() : tags.Split(',').Select(t => t.Trim()).ToList()
    };
    
    var stored = await indexer.AddMemoryAsync(memory);
    
    Console.WriteLine($"✓ Memory stored successfully");
    Console.WriteLine($"  ID: {stored.Id}");
    Console.WriteLine($"  Alias: {stored.Alias ?? "(none)"}");
    Console.WriteLine($"  Name: {stored.MemoryName}");
    Console.WriteLine($"  Tags: {string.Join(", ", stored.Tags)}");
    Console.WriteLine($"  Size: {stored.SizeInKBytes:F2} KB ({stored.LinesCount} lines)");
}

async Task RunMemoryAppend(string project, string parentIdOrAlias, string name, string content, string? tags)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    // Get parent memory to inherit tags if needed
    var parentMemory = await indexer.GetMemoryByIdOrAliasAsync(parentIdOrAlias);
    if (parentMemory == null)
    {
        Console.Error.WriteLine($"Parent memory '{parentIdOrAlias}' not found in project '{project}'");
        return;
    }
    
    // Build tag list
    var tagList = new List<string>();
    tagList.AddRange(parentMemory.Tags); // Inherit parent tags
    
    if (!string.IsNullOrEmpty(tags))
    {
        var newTags = tags.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t));
        foreach (var tag in newTags)
        {
            if (!tagList.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                tagList.Add(tag);
            }
        }
    }
    
    var memory = new CSharpMcpServer.Models.Memory
    {
        ProjectName = project,
        MemoryName = name,
        FullDocumentText = content,
        Tags = tagList,
        ParentMemoryId = parentMemory.Id
    };
    
    var stored = await indexer.AddMemoryAsync(memory);
    
    Console.WriteLine($"✓ Child memory appended successfully");
    Console.WriteLine($"  ID: {stored.Id}");
    Console.WriteLine($"  Name: {stored.MemoryName}");
    Console.WriteLine($"  Alias: {stored.Alias ?? "(none)"}");
    Console.WriteLine($"  Parent: '{parentIdOrAlias}' (ID: {parentMemory.Id})");
    Console.WriteLine($"  Tags: {string.Join(", ", stored.Tags)}");
    Console.WriteLine($"  Size: {stored.SizeInKBytes:F2} KB ({stored.LinesCount} lines)");
}

async Task RunMemorySearch(string project, string query, int topK)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    var searchConfig = new CSharpMcpServer.Models.MemorySearchConfig
    {
        TopK = topK,
        SnippetLineCount = config.Memory.DefaultSnippetLines
    };
    
    var sw = Stopwatch.StartNew();
    var results = await indexer.SearchMemoriesAsync(query, searchConfig);
    sw.Stop();
    
    Console.WriteLine($"\nSearch completed in {sw.ElapsedMilliseconds}ms");
    Console.WriteLine($"Query: \"{query}\"");
    Console.WriteLine($"Results: {results.Count}\n");
    
    foreach (var result in results)
    {
        Console.WriteLine($"--- Memory: {result.Memory.MemoryName} (Score: {result.Score:F4}) ---");
        Console.WriteLine($"ID: {result.Memory.Id}");
        Console.WriteLine($"Tags: {string.Join(", ", result.Memory.Tags)}");
        Console.WriteLine($"Age: {result.Memory.AgeDisplay}");
        Console.WriteLine($"Size: {result.Memory.SizeInKBytes:F2} KB ({result.Memory.LinesCount} lines)");
        Console.WriteLine($"\nSnippet ({result.SnippetLineCount} lines):");
        Console.WriteLine(result.SnippetText);
        Console.WriteLine();
    }
}

async Task RunMemoryGet(string project, string memoryIdOrAlias)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    var memory = await indexer.GetMemoryByIdOrAliasAsync(memoryIdOrAlias);
    if (memory == null)
    {
        Console.Error.WriteLine($"Memory '{memoryIdOrAlias}' not found in project '{project}'");
        return;
    }
    
    Console.WriteLine($"=== Memory: {memory.MemoryName} ===");
    Console.WriteLine($"ID: {memory.Id}");
    Console.WriteLine($"Alias: {memory.Alias ?? "(none)"}");
    Console.WriteLine($"Project: {memory.ProjectName}");
    Console.WriteLine($"Tags: {string.Join(", ", memory.Tags)}");
    Console.WriteLine($"Created: {memory.Timestamp:yyyy-MM-dd HH:mm:ss} UTC ({memory.AgeDisplay})");
    Console.WriteLine($"Size: {memory.SizeInKBytes:F2} KB ({memory.LinesCount} lines)");
    
    if (memory.ParentMemoryId.HasValue)
    {
        Console.WriteLine($"Parent: memory {memory.ParentMemoryId.Value}");
    }
    
    if (memory.ChildMemoryIds.Any())
    {
        Console.WriteLine($"Children: {memory.ChildMemoryIds.Count}");
    }
    
    Console.WriteLine($"\n--- Content ---");
    Console.WriteLine(memory.FullDocumentText);
}

async Task RunMemoryList(string project, string? tagFilter)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    var memories = await indexer.GetAllMemoriesAsync();
    
    if (!string.IsNullOrEmpty(tagFilter))
    {
        var tags = tagFilter.Split(',').Select(t => t.Trim().ToLower()).ToList();
        memories = memories.Where(m => m.Tags.Any(t => tags.Contains(t.ToLower()))).ToList();
    }
    
    Console.WriteLine($"=== Memories in project '{project}' ===");
    if (!string.IsNullOrEmpty(tagFilter))
    {
        Console.WriteLine($"Filter: {tagFilter}");
    }
    Console.WriteLine($"Total: {memories.Count}\n");
    
    foreach (var memory in memories.OrderByDescending(m => m.Timestamp))
    {
        Console.WriteLine($"• {memory.MemoryName}");
        Console.WriteLine($"  ID: {memory.Id}");
        Console.WriteLine($"  Alias: {memory.Alias ?? "(none)"}");
        Console.WriteLine($"  Tags: {string.Join(", ", memory.Tags)}");
        Console.WriteLine($"  Size: {memory.SizeInKBytes:F2} KB ({memory.LinesCount} lines)");
        Console.WriteLine($"  Age: {memory.AgeDisplay}");
        
        if (memory.ParentMemoryId.HasValue || memory.ChildMemoryIds.Any())
        {
            var relations = new List<string>();
            if (memory.ParentMemoryId.HasValue) relations.Add($"parent: {memory.ParentMemoryId.Value}");
            if (memory.ChildMemoryIds.Any()) relations.Add($"{memory.ChildMemoryIds.Count} children");
            Console.WriteLine($"  Relations: {string.Join(", ", relations)}");
        }
        
        Console.WriteLine();
    }
}

async Task RunMemoryDelete(string project, string memoryIdOrAlias)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    // First get the memory to check if it exists and get its ID
    var memory = await indexer.GetMemoryByIdOrAliasAsync(memoryIdOrAlias);
    if (memory == null)
    {
        Console.Error.WriteLine($"Memory '{memoryIdOrAlias}' not found in project '{project}'");
        return;
    }
    
    var deleted = await indexer.DeleteMemoryAsync(memory.Id);
    
    if (deleted)
    {
        Console.WriteLine($"✓ Memory '{memoryIdOrAlias}' (ID: {memory.Id}) deleted successfully");
    }
    else
    {
        Console.Error.WriteLine($"Failed to delete memory '{memoryIdOrAlias}' (ID: {memory.Id})");
    }
}

async Task RunMemoryStats(string project)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    var stats = indexer.GetMemoryStats();
    var memories = await indexer.GetAllMemoriesAsync();
    
    Console.WriteLine($"=== Memory Statistics for '{project}' ===\n");
    
    Console.WriteLine("Vector Store:");
    Console.WriteLine($"  Vectors: {stats.VectorCount}");
    Console.WriteLine($"  Dimension: {stats.DimensionSize}");
    Console.WriteLine($"  Memory Usage: {stats.TotalMemoryMB:F2} MB");
    Console.WriteLine($"  Precision: {stats.Precision}");
    Console.WriteLine($"  Search Method: {stats.SearchMethod}");
    
    Console.WriteLine("\nContent Statistics:");
    Console.WriteLine($"  Total Memories: {memories.Count}");
    Console.WriteLine($"  Total Size: {memories.Sum(m => m.SizeInKBytes):F2} KB");
    Console.WriteLine($"  Total Lines: {memories.Sum(m => m.LinesCount):N0}");
    
    if (memories.Any())
    {
        Console.WriteLine($"  Average Size: {memories.Average(m => m.SizeInKBytes):F2} KB");
        Console.WriteLine($"  Average Lines: {memories.Average(m => m.LinesCount):F1}");
    }
    
    // Tag statistics
    var tagCounts = memories
        .SelectMany(m => m.Tags)
        .GroupBy(t => t.ToLower())
        .OrderByDescending(g => g.Count())
        .Take(10)
        .ToList();
    
    if (tagCounts.Any())
    {
        Console.WriteLine("\nTop Tags:");
        foreach (var tag in tagCounts)
        {
            Console.WriteLine($"  {tag.Key}: {tag.Count()}");
        }
    }
    
    // Graph statistics
    var withParent = memories.Count(m => m.ParentMemoryId.HasValue);
    var withChildren = memories.Count(m => m.ChildMemoryIds.Any());
    
    if (withParent > 0 || withChildren > 0)
    {
        Console.WriteLine("\nGraph Relations:");
        Console.WriteLine($"  Memories with parent: {withParent}");
        Console.WriteLine($"  Memories with children: {withChildren}");
        Console.WriteLine($"  Total child links: {memories.Sum(m => m.ChildMemoryIds.Count)}");
    }
}

async Task RunMemoryAddFile(string project, string name, string filePath, string? tags, string? parentIdStr)
{
    try
    {
        // Validate and read the file
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"ERROR: File not found: {filePath}");
            return;
        }
        
        var fileInfo = new FileInfo(filePath);
        
        // Check file size (warn if over 1MB, error if over 10MB)
        const long maxSizeBytes = 10 * 1024 * 1024; // 10MB
        const long warnSizeBytes = 1 * 1024 * 1024; // 1MB
        
        if (fileInfo.Length > maxSizeBytes)
        {
            Console.Error.WriteLine($"ERROR: File too large ({fileInfo.Length:N0} bytes). Maximum allowed: {maxSizeBytes:N0} bytes ({maxSizeBytes / (1024 * 1024)}MB)");
            return;
        }
        
        if (fileInfo.Length > warnSizeBytes)
        {
            Console.WriteLine($"WARNING: Large file detected ({fileInfo.Length:N0} bytes, {fileInfo.Length / 1024.0:F1} KB)");
        }
        
        // Read file content
        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to read file '{filePath}': {ex.Message}");
            return;
        }
        
        if (string.IsNullOrEmpty(content))
        {
            Console.Error.WriteLine($"ERROR: File '{filePath}' is empty");
            return;
        }
        
        // Parse parent ID if provided
        int? parentId = null;
        if (!string.IsNullOrEmpty(parentIdStr))
        {
            if (!int.TryParse(parentIdStr, out var pid))
            {
                Console.Error.WriteLine($"ERROR: Invalid parent memory ID: {parentIdStr}. Memory IDs must be integers.");
                return;
            }
            parentId = pid;
        }
        
        var config = Configuration.Load();
        using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
        
        // If parent ID provided, validate it exists and inherit tags
        List<string> tagList = new();
        if (parentId.HasValue)
        {
            var parentMemory = await indexer.GetMemoryAsync(parentId.Value);
            if (parentMemory == null)
            {
                Console.Error.WriteLine($"ERROR: Parent memory {parentId.Value} not found in project '{project}'");
                return;
            }
            
            // Inherit parent tags
            tagList.AddRange(parentMemory.Tags);
            Console.WriteLine($"Inheriting tags from parent memory {parentId.Value}: {string.Join(", ", parentMemory.Tags)}");
        }
        
        // Add additional tags if provided
        if (!string.IsNullOrEmpty(tags))
        {
            var newTags = tags.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t));
            foreach (var tag in newTags)
            {
                if (!tagList.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    tagList.Add(tag);
                }
            }
        }
        
        // Auto-add file-based tags
        var extension = Path.GetExtension(filePath).ToLower();
        if (!string.IsNullOrEmpty(extension))
        {
            var extensionTag = $"file-{extension.TrimStart('.')}";
            if (!tagList.Contains(extensionTag, StringComparer.OrdinalIgnoreCase))
            {
                tagList.Add(extensionTag);
            }
        }
        
        // Add filename tag
        var filenameTag = $"filename-{Path.GetFileNameWithoutExtension(filePath)}";
        if (!tagList.Contains(filenameTag, StringComparer.OrdinalIgnoreCase))
        {
            tagList.Add(filenameTag);
        }
        
        // Create memory object
        var memory = new CSharpMcpServer.Models.Memory
        {
            ProjectName = project,
            MemoryName = name,
            FullDocumentText = content,
            Tags = tagList,
            ParentMemoryId = parentId
        };
        
        Console.WriteLine($"Reading file: {filePath}");
        Console.WriteLine($"File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0:F1} KB)");
        Console.WriteLine($"Content length: {content.Length:N0} characters");
        Console.WriteLine($"Line count: {content.Split('\n').Length}");
        
        // Store the memory
        var stored = await indexer.AddMemoryAsync(memory);
        
        Console.WriteLine($"\n✓ Memory stored successfully from file");
        Console.WriteLine($"  ID: {stored.Id}");
        Console.WriteLine($"  Alias: {stored.Alias ?? "(none)"}");
        Console.WriteLine($"  Name: {stored.MemoryName}");
        Console.WriteLine($"  Source: {filePath}");
        if (parentId.HasValue)
        {
            Console.WriteLine($"  Parent: memory {parentId.Value}");
        }
        Console.WriteLine($"  Tags: {string.Join(", ", stored.Tags)}");
        Console.WriteLine($"  Size: {stored.SizeInKBytes:F2} KB ({stored.LinesCount} lines)");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
        }
    }
}

async Task RunMemoryTree(string project, string? rootIdOrAlias, int maxDepth, bool includeContent)
{
    var config = Configuration.Load();
    var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<CSharpMcpServer.Protocol.MemoryTools>();
    var tools = new CSharpMcpServer.Protocol.MemoryTools(config, logger);
    
    try
    {
        var result = await tools.GetMemoryTree(project, rootIdOrAlias, maxDepth, includeContent, 100);
        
        dynamic dynResult = result;
        if (dynResult.status == "success")
        {
            Console.WriteLine($"=== Memory Tree for '{project}' ===");
            Console.WriteLine($"Root memories: {dynResult.rootCount}");
            Console.WriteLine($"Total memories: {dynResult.totalMemories}\n");
            Console.WriteLine(dynResult.tree);
        }
        else if (dynResult.status == "not_found")
        {
            Console.Error.WriteLine($"Error: {dynResult.message}");
        }
        else
        {
            Console.Error.WriteLine($"Error: {dynResult.message}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error getting memory tree: {ex.Message}");
    }
}

async Task RunMemoryUpdate(string project, string memoryIdOrAlias, string[] updateArgs)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    // Parse command line arguments
    string? newName = null;
    string? newContent = null;
    string? newTags = null;
    
    for (int i = 0; i < updateArgs.Length; i++)
    {
        if (updateArgs[i] == "--name" && i + 1 < updateArgs.Length)
        {
            newName = updateArgs[++i];
        }
        else if (updateArgs[i] == "--content" && i + 1 < updateArgs.Length)
        {
            newContent = updateArgs[++i];
        }
        else if (updateArgs[i] == "--tags" && i + 1 < updateArgs.Length)
        {
            newTags = updateArgs[++i];
        }
    }
    
    if (newName == null && newContent == null && newTags == null)
    {
        Console.Error.WriteLine("Error: At least one of --name, --content, or --tags must be specified");
        return;
    }
    
    // Get the existing memory first to show what's changing
    var existingMemory = await indexer.GetMemoryByIdOrAliasAsync(memoryIdOrAlias);
    if (existingMemory == null)
    {
        Console.Error.WriteLine($"Memory '{memoryIdOrAlias}' not found in project '{project}'");
        return;
    }
    
    Console.WriteLine($"Updating memory '{existingMemory.MemoryName}' (ID: {existingMemory.Id})...");
    
    // Parse tags if provided
    List<string>? tagList = null;
    if (newTags != null)
    {
        tagList = newTags.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
    }
    
    // Update the memory
    var updatedMemory = await indexer.UpdateMemoryAsync(existingMemory.Id, newName, newContent, tagList);
    
    if (updatedMemory != null)
    {
        Console.WriteLine($"✓ Memory updated successfully");
        
        // Show what changed
        if (newName != null && newName != existingMemory.MemoryName)
        {
            Console.WriteLine($"  Name: {existingMemory.MemoryName} → {updatedMemory.MemoryName}");
            if (updatedMemory.Alias != existingMemory.Alias)
            {
                Console.WriteLine($"  Alias: @{existingMemory.Alias} → @{updatedMemory.Alias}");
            }
        }
        
        if (newContent != null && newContent != existingMemory.FullDocumentText)
        {
            Console.WriteLine($"  Content: Updated ({existingMemory.LinesCount} → {updatedMemory.LinesCount} lines, {existingMemory.SizeInKBytes:F2} → {updatedMemory.SizeInKBytes:F2} KB)");
        }
        
        if (tagList != null && !tagList.SequenceEqual(existingMemory.Tags))
        {
            Console.WriteLine($"  Tags: {string.Join(", ", existingMemory.Tags)} → {string.Join(", ", updatedMemory.Tags)}");
        }
    }
    else
    {
        Console.Error.WriteLine("Failed to update memory");
    }
}

async Task RunMemoryExport(string project, string? rootIdOrAlias, string format, string? outputFile)
{
    var config = Configuration.Load();
    var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<CSharpMcpServer.Protocol.MemoryTools>();
    var tools = new CSharpMcpServer.Protocol.MemoryTools(config, logger);
    
    try
    {
        // Handle empty string as null for rootIdOrAlias
        if (string.IsNullOrEmpty(rootIdOrAlias))
            rootIdOrAlias = null;
            
        Console.WriteLine($"Exporting memories from '{project}' as {format}...");
        
        var result = await tools.ExportMemoryTree(project, rootIdOrAlias, format, true, 10, true);
        
        dynamic dynResult = result;
        if (dynResult.status == "success")
        {
            string content = dynResult.content;
            
            if (!string.IsNullOrEmpty(outputFile))
            {
                // Write to file
                await File.WriteAllTextAsync(outputFile, content);
                Console.WriteLine($"✓ Export successful!");
                Console.WriteLine($"  Format: {dynResult.format}");
                Console.WriteLine($"  Root memories: {dynResult.rootCount}");
                Console.WriteLine($"  Total exported: {dynResult.totalExported}");
                Console.WriteLine($"  File size: {dynResult.contentLength:N0} bytes");
                Console.WriteLine($"  Output file: {outputFile}");
            }
            else
            {
                // Output to console
                Console.WriteLine($"=== Memory Export ({dynResult.format}) ===");
                Console.WriteLine($"Root memories: {dynResult.rootCount}");
                Console.WriteLine($"Total exported: {dynResult.totalExported}");
                Console.WriteLine($"Content length: {dynResult.contentLength:N0} bytes\n");
                Console.WriteLine(content);
            }
        }
        else if (dynResult.status == "not_found")
        {
            Console.Error.WriteLine($"Error: {dynResult.message}");
        }
        else
        {
            Console.Error.WriteLine($"Error: {dynResult.message}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error exporting memory tree: {ex.Message}");
    }
}

async Task RunMemoryImportMarkdown(string project, string markdownFile, string? tags, string? parentIdStr)
{
    try
    {
        // Validate file exists
        if (!File.Exists(markdownFile))
        {
            Console.Error.WriteLine($"ERROR: File not found: {markdownFile}");
            return;
        }
        
        var fileInfo = new FileInfo(markdownFile);
        Console.WriteLine($"Importing markdown file: {markdownFile}");
        Console.WriteLine($"File size: {fileInfo.Length:N0} bytes ({fileInfo.Length / 1024.0:F1} KB)");
        
        // Parse parent ID if provided
        int? parentId = null;
        if (!string.IsNullOrEmpty(parentIdStr))
        {
            if (!int.TryParse(parentIdStr, out var pid))
            {
                Console.Error.WriteLine($"ERROR: Invalid parent memory ID: {parentIdStr}. Memory IDs must be integers.");
                return;
            }
            parentId = pid;
        }
        
        // Use MCP tool functionality directly
        var config = Configuration.Load();
        var logger = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<CSharpMcpServer.Protocol.MemoryTools>();
        var memoryTools = new CSharpMcpServer.Protocol.MemoryTools(config, logger);
        
        Console.WriteLine($"Processing markdown structure...");
        
        // Call the ImportMarkdownAsMemories tool
        var result = await memoryTools.ImportMarkdownAsMemories(project, markdownFile, parentId, tags);
        
        // Handle the result dynamically based on its type
        dynamic dynResult = result;
        try
        {
            if (dynResult.status == "success")
            {
                Console.WriteLine($"\n✓ {dynResult.message}");
                Console.WriteLine($"  Imported memories: {dynResult.importedCount}");
                Console.WriteLine($"  Total size: {dynResult.totalSizeKB} KB");
                
                if (dynResult.tags != null)
                {
                    var tagList = new List<string>();
                    foreach (var tag in dynResult.tags)
                    {
                        tagList.Add(tag.ToString());
                    }
                    Console.WriteLine($"  Tags applied: {string.Join(", ", tagList)}");
                }
                
                // Display hierarchy
                if (dynResult.hierarchy != null)
                {
                    Console.WriteLine($"\nImported hierarchy:");
                    foreach (dynamic memory in dynResult.hierarchy)
                    {
                        var level = (int)memory.level;
                        var indent = new string(' ', (level - 1) * 2);
                        
                        Console.WriteLine($"{indent}• [{memory.id}] {memory.name}");
                        Console.WriteLine($"{indent}  alias: {memory.alias}");
                        Console.WriteLine($"{indent}  size: {memory.sizeKB} KB, lines: {memory.lines}");
                    }
                }
            }
            else if (dynResult.status == "error")
            {
                Console.Error.WriteLine($"ERROR: {dynResult.message}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR parsing result: {ex.Message}");
            Console.WriteLine($"Result type: {result?.GetType().FullName}");
            Console.WriteLine($"Result: {System.Text.Json.JsonSerializer.Serialize(result)}");
        }
        
        memoryTools.Dispose();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        if (ex.InnerException != null)
        {
            Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
        }
    }
}