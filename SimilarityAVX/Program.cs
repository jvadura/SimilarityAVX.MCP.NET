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
                    Console.Error.WriteLine("Usage: memory-get <project> <memoryId>");
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
                    Console.Error.WriteLine("Usage: memory-delete <project> <memoryId>");
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
    Console.WriteLine("  dotnet run -- memory-add <project> <name> <content> [tags]  # Add a memory");
    Console.WriteLine("  dotnet run -- memory-search <project> <query> [topK]        # Search memories");
    Console.WriteLine("  dotnet run -- memory-get <project> <memoryId>               # Get a memory");
    Console.WriteLine("  dotnet run -- memory-list <project> [tagFilter]             # List memories");
    Console.WriteLine("  dotnet run -- memory-delete <project> <memoryId>            # Delete a memory");
    Console.WriteLine("  dotnet run -- memory-stats <project>                        # Memory statistics");
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
    Console.WriteLine($"  Name: {stored.MemoryName}");
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

async Task RunMemoryGet(string project, string memoryId)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    var memory = await indexer.GetMemoryAsync(memoryId);
    if (memory == null)
    {
        Console.Error.WriteLine($"Memory {memoryId} not found in project '{project}'");
        return;
    }
    
    Console.WriteLine($"=== Memory: {memory.MemoryName} ===");
    Console.WriteLine($"ID: {memory.Id}");
    Console.WriteLine($"Project: {memory.ProjectName}");
    Console.WriteLine($"Tags: {string.Join(", ", memory.Tags)}");
    Console.WriteLine($"Created: {memory.Timestamp:yyyy-MM-dd HH:mm:ss} UTC ({memory.AgeDisplay})");
    Console.WriteLine($"Size: {memory.SizeInKBytes:F2} KB ({memory.LinesCount} lines)");
    
    if (!string.IsNullOrEmpty(memory.ParentMemoryId))
    {
        Console.WriteLine($"Parent: {memory.ParentMemoryId}");
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
        Console.WriteLine($"  Tags: {string.Join(", ", memory.Tags)}");
        Console.WriteLine($"  Size: {memory.SizeInKBytes:F2} KB ({memory.LinesCount} lines)");
        Console.WriteLine($"  Age: {memory.AgeDisplay}");
        
        if (!string.IsNullOrEmpty(memory.ParentMemoryId) || memory.ChildMemoryIds.Any())
        {
            var relations = new List<string>();
            if (!string.IsNullOrEmpty(memory.ParentMemoryId)) relations.Add("has parent");
            if (memory.ChildMemoryIds.Any()) relations.Add($"{memory.ChildMemoryIds.Count} children");
            Console.WriteLine($"  Relations: {string.Join(", ", relations)}");
        }
        
        Console.WriteLine();
    }
}

async Task RunMemoryDelete(string project, string memoryId)
{
    var config = Configuration.Load();
    using var indexer = new CSharpMcpServer.Core.MemoryIndexer(project, config);
    
    var deleted = await indexer.DeleteMemoryAsync(memoryId);
    
    if (deleted)
    {
        Console.WriteLine($"✓ Memory {memoryId} deleted successfully");
    }
    else
    {
        Console.Error.WriteLine($"Memory {memoryId} not found in project '{project}'");
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
    var withParent = memories.Count(m => !string.IsNullOrEmpty(m.ParentMemoryId));
    var withChildren = memories.Count(m => m.ChildMemoryIds.Any());
    
    if (withParent > 0 || withChildren > 0)
    {
        Console.WriteLine("\nGraph Relations:");
        Console.WriteLine($"  Memories with parent: {withParent}");
        Console.WriteLine($"  Memories with children: {withChildren}");
        Console.WriteLine($"  Total child links: {memories.Sum(m => m.ChildMemoryIds.Count)}");
    }
}