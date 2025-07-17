using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CSharpMcpServer.Core;
using CSharpMcpServer.Models;
using CSharpMcpServer.Storage;
using CSharpMcpServer.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CSharpMcpServer.Protocol
{
    /// <summary>
    /// MCP tools for memory management operations
    /// </summary>
    [McpServerToolType]
    public partial class MemoryTools
    {
        private readonly Dictionary<string, MemoryIndexer> _memoryIndexers = new();
        private readonly Configuration _configuration;
        private readonly ILogger<MemoryTools> _logger;
        
        public MemoryTools(Configuration configuration, ILogger<MemoryTools> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }
        
        /// <summary>
        /// Add a new memory to the system
        /// </summary>
        [McpServerTool]
        [Description("Store a new memory with tags and metadata. Memories are persistent knowledge entries that can be searched semantically. Each memory is project-specific and includes timestamps for temporal context.")]
        public async Task<object> AddMemory(
            [Description("Unique project identifier for memory isolation")] string project,
            [Description("Descriptive name for the memory (e.g., 'API Design Decisions', 'User Requirements')")] string memoryName,
            [Description("Full text content of the memory. Can be multiple paragraphs or structured text.")] string content,
            [Description("Comma-separated tags for categorization (e.g., 'architecture,api,decisions')")] string? tags = null,
            [Description("Optional parent memory ID to create hierarchical relationships")] string? parentMemoryId = null)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                
                var memory = new Memory
                {
                    ProjectName = project,
                    MemoryName = memoryName,
                    FullDocumentText = content,
                    Tags = string.IsNullOrEmpty(tags) ? new List<string>() : 
                           tags.Split(',').Select(t => t.Trim()).ToList(),
                    ParentMemoryId = parentMemoryId
                };
                
                var stored = await indexer.AddMemoryAsync(memory);
                
                return new
                {
                    status = "success",
                    memoryId = stored.Id,
                    message = $"Memory '{memoryName}' stored successfully",
                    metadata = new
                    {
                        linesCount = stored.LinesCount,
                        sizeKB = Math.Round(stored.SizeInKBytes, 2),
                        tags = stored.Tags,
                        timestamp = stored.Timestamp.ToString("O")
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding memory");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Delete a memory from the system
        /// </summary>
        [McpServerTool]
        [Description("Delete a memory by its ID. This permanently removes the memory and its embeddings from the system.")]
        public async Task<object> DeleteMemory(
            [Description("Project name")] string project,
            [Description("The unique ID of the memory to delete")] string memoryId)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                var deleted = await indexer.DeleteMemoryAsync(memoryId);
                
                if (deleted)
                {
                    return new
                    {
                        status = "success",
                        message = $"Memory {memoryId} deleted successfully"
                    };
                }
                else
                {
                    return new
                    {
                        status = "not_found",
                        message = $"Memory {memoryId} not found in project '{project}'"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting memory");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Retrieve a memory by ID
        /// </summary>
        [McpServerTool]
        [Description("Retrieve the full content of a memory by its ID. Returns complete text, metadata, and graph relationships if available.")]
        public async Task<object> GetMemory(
            [Description("Project name")] string project,
            [Description("The unique ID of the memory to retrieve")] string memoryId,
            [Description("Include child memories in response (default: true)")] bool includeChildren = true)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                var memory = await indexer.GetMemoryAsync(memoryId);
                
                if (memory == null)
                {
                    return new
                    {
                        status = "not_found",
                        message = $"Memory {memoryId} not found in project '{project}'"
                    };
                }
                
                var result = new
                {
                    status = "success",
                    memory = new
                    {
                        id = memory.Id,
                        name = memory.MemoryName,
                        content = memory.FullDocumentText,
                        tags = memory.Tags,
                        age = memory.AgeDisplay,
                        timestamp = memory.Timestamp.ToString("O"),
                        metadata = new
                        {
                            linesCount = memory.LinesCount,
                            sizeKB = Math.Round(memory.SizeInKBytes, 2)
                        },
                        parentMemoryId = memory.ParentMemoryId
                    }
                };
                
                // Include children if requested
                if (includeChildren && memory.ChildMemoryIds.Any())
                {
                    var storage = new MemoryStorage(project);
                    var children = await storage.GetChildMemoriesAsync(memory.Id, 10);
                    
                    ((dynamic)result).memory.children = children.Select(c => new
                    {
                        id = c.Id,
                        name = c.MemoryName,
                        age = c.AgeDisplay,
                        tags = c.Tags
                    }).ToList();
                    
                    storage.Dispose();
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving memory");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Search memories using semantic similarity
        /// </summary>
        [McpServerTool]
        [Description("Search memories using natural language queries. Returns top matches with similarity scores, snippets, and metadata to help identify relevant memories. Great for finding related concepts, past decisions, or documented knowledge.")]
        public async Task<object> SearchMemories(
            [Description("Project name to search within")] string project,
            [Description("Natural language search query (e.g., 'authentication flow', 'API design decisions')")] string query,
            [Description("Number of top results to return (default: 3)")] int topK = 3,
            [Description("Number of lines to include in each snippet (default: 10)")] int snippetLines = 10)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                
                var config = new MemorySearchConfig
                {
                    TopK = topK,
                    SnippetLineCount = snippetLines,
                    IncludeMetadata = true,
                    IncludeGraphRelations = false // Keep simple for now
                };
                
                var results = await indexer.SearchMemoriesAsync(query, config);
                
                if (!results.Any())
                {
                    return new
                    {
                        status = "success",
                        message = "No memories found matching your query",
                        results = new List<object>()
                    };
                }
                
                return new
                {
                    status = "success",
                    query = query,
                    resultCount = results.Count,
                    results = results.Select(r => new
                    {
                        memory = new
                        {
                            id = r.Memory.Id,
                            name = r.Memory.MemoryName,
                            tags = r.Memory.Tags,
                            age = r.Memory.AgeDisplay
                        },
                        score = Math.Round(r.Score, 4),
                        snippet = r.SnippetText,
                        metadata = new
                        {
                            totalLines = r.Memory.LinesCount,
                            totalSizeKB = Math.Round(r.Memory.SizeInKBytes, 2),
                            snippetLines = r.SnippetLineCount
                        }
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching memories");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// List all memories in a project
        /// </summary>
        [McpServerTool]
        [Description("List all memories stored in a project. Returns memory names, tags, and ages for quick overview.")]
        public async Task<object> ListMemories(
            [Description("Project name")] string project,
            [Description("Optional tag filter (comma-separated)")] string? filterTags = null)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                var memories = await indexer.GetAllMemoriesAsync();
                
                // Apply tag filter if provided
                if (!string.IsNullOrEmpty(filterTags))
                {
                    var tags = filterTags.Split(',').Select(t => t.Trim().ToLower()).ToList();
                    memories = memories.Where(m => 
                        m.Tags.Any(t => tags.Contains(t.ToLower()))
                    ).ToList();
                }
                
                return new
                {
                    status = "success",
                    project = project,
                    totalCount = memories.Count,
                    memories = memories.Select(m => new
                    {
                        id = m.Id,
                        name = m.MemoryName,
                        tags = m.Tags,
                        age = m.AgeDisplay,
                        linesCount = m.LinesCount,
                        sizeKB = Math.Round(m.SizeInKBytes, 2),
                        hasParent = !string.IsNullOrEmpty(m.ParentMemoryId),
                        childCount = m.ChildMemoryIds.Count
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing memories");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Get memory statistics for a project
        /// </summary>
        [McpServerTool]
        [Description("Get memory system statistics including vector count, memory usage, and performance metrics.")]
        public async Task<object> GetMemoryStats(
            [Description("Project name")] string project)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                var stats = indexer.GetMemoryStats();
                var memories = await indexer.GetAllMemoriesAsync();
                
                // Calculate tag statistics
                var tagCounts = memories
                    .SelectMany(m => m.Tags)
                    .GroupBy(t => t.ToLower())
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => new { tag = g.Key, count = g.Count() })
                    .ToList();
                
                return new
                {
                    status = "success",
                    project = project,
                    memoryCount = memories.Count,
                    vectorStats = new
                    {
                        vectorCount = stats.VectorCount,
                        dimension = stats.DimensionSize,
                        memoryUsageMB = Math.Round(stats.TotalMemoryMB, 2),
                        precision = stats.Precision,
                        searchMethod = stats.SearchMethod
                    },
                    contentStats = new
                    {
                        totalSizeKB = Math.Round(memories.Sum(m => m.SizeInKBytes), 2),
                        totalLines = memories.Sum(m => m.LinesCount),
                        averageSizeKB = memories.Any() ? 
                            Math.Round(memories.Average(m => m.SizeInKBytes), 2) : 0,
                        averageLines = memories.Any() ? 
                            Math.Round(memories.Average(m => m.LinesCount), 1) : 0
                    },
                    topTags = tagCounts,
                    graphStats = new
                    {
                        memoriesWithParent = memories.Count(m => !string.IsNullOrEmpty(m.ParentMemoryId)),
                        memoriesWithChildren = memories.Count(m => m.ChildMemoryIds.Any()),
                        totalChildLinks = memories.Sum(m => m.ChildMemoryIds.Count)
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting memory stats");
                return new { status = "error", message = ex.Message };
            }
        }
        
        private MemoryIndexer GetOrCreateMemoryIndexer(string project)
        {
            if (!_memoryIndexers.TryGetValue(project, out var indexer))
            {
                indexer = new MemoryIndexer(project, _configuration);
                _memoryIndexers[project] = indexer;
            }
            return indexer;
        }
        
        /// <summary>
        /// Dispose all memory indexers
        /// </summary>
        public void Dispose()
        {
            foreach (var indexer in _memoryIndexers.Values)
            {
                indexer.Dispose();
            }
            _memoryIndexers.Clear();
        }
    }
}