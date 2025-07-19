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
        public async Task<object> MemoryAdd(
            [Description("Unique project identifier for memory isolation")] string project,
            [Description("Descriptive name for the memory (e.g., 'API Design Decisions', 'User Requirements')")] string memoryName,
            [Description("Full text content of the memory. Can be multiple paragraphs or structured text.")] string content,
            [Description("Comma-separated tags for categorization (e.g., 'architecture,api,decisions')")] string? tags = null,
            [Description("Optional parent memory ID to create hierarchical relationships")] int? parentMemoryId = null)
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
                    alias = stored.Alias,
                    message = $"Memory '{memoryName}' stored successfully with alias '{stored.Alias}'",
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
        /// Update an existing memory
        /// </summary>
        [McpServerTool]
        [Description("Update an existing memory's name, content, or tags. Accepts memory ID or alias. When content is updated, the embedding is regenerated. Returns the updated memory with new metadata.")]
        public async Task<object> MemoryUpdate(
            [Description("Project name")] string project,
            [Description("The unique ID or alias of the memory to update (e.g., 42 or 'api-design')")] string memoryIdOrAlias,
            [Description("New memory name (null to keep existing)")] string? newName = null,
            [Description("New content (null to keep existing)")] string? newContent = null,
            [Description("New tags comma-separated (null to keep existing)")] string? newTags = null)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                
                // First get the memory to check if it exists and get its ID
                var memory = await indexer.GetMemoryByIdOrAliasAsync(memoryIdOrAlias);
                if (memory == null)
                {
                    return new
                    {
                        status = "not_found",
                        message = $"Memory '{memoryIdOrAlias}' not found in project '{project}'"
                    };
                }
                
                // Parse tags if provided
                List<string>? tagList = null;
                if (newTags != null)
                {
                    tagList = newTags.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                }
                
                // Update the memory
                var updatedMemory = await indexer.UpdateMemoryAsync(memory.Id, newName, newContent, tagList);
                
                if (updatedMemory == null)
                {
                    return new
                    {
                        status = "error",
                        message = "Failed to update memory"
                    };
                }
                
                return new
                {
                    status = "success",
                    message = $"Memory '{memoryIdOrAlias}' updated successfully",
                    memory = new
                    {
                        id = updatedMemory.Id,
                        name = updatedMemory.MemoryName,
                        alias = updatedMemory.Alias,
                        tags = updatedMemory.Tags,
                        metadata = new
                        {
                            linesCount = updatedMemory.LinesCount,
                            sizeKB = Math.Round(updatedMemory.SizeInKBytes, 2),
                            timestamp = updatedMemory.Timestamp.ToString("O")
                        }
                    },
                    changes = new
                    {
                        nameChanged = newName != null && newName != memory.MemoryName,
                        contentChanged = newContent != null && newContent != memory.FullDocumentText,
                        tagsChanged = tagList != null && !tagList.SequenceEqual(memory.Tags),
                        aliasChanged = updatedMemory.Alias != memory.Alias
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating memory");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Delete a memory from the system
        /// </summary>
        [McpServerTool]
        [Description("Delete a memory by its ID or alias. This permanently removes the memory and its embeddings from the system.")]
        public async Task<object> MemoryDelete(
            [Description("Project name")] string project,
            [Description("The unique ID or alias of the memory to delete (e.g., 42 or 'api-design')")] string memoryIdOrAlias)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                
                // First get the memory to check if it exists and get its ID
                var memory = await indexer.GetMemoryByIdOrAliasAsync(memoryIdOrAlias);
                if (memory == null)
                {
                    return new
                    {
                        status = "not_found",
                        message = $"Memory '{memoryIdOrAlias}' not found in project '{project}'"
                    };
                }
                
                var deleted = await indexer.DeleteMemoryAsync(memory.Id);
                
                if (deleted)
                {
                    return new
                    {
                        status = "success",
                        message = $"Memory '{memoryIdOrAlias}' (ID: {memory.Id}) deleted successfully"
                    };
                }
                else
                {
                    return new
                    {
                        status = "error",
                        message = $"Failed to delete memory '{memoryIdOrAlias}'"
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
        /// Retrieve a memory by ID or alias
        /// </summary>
        [McpServerTool]
        [Description("Retrieve the full content of a memory by its ID or alias. Accepts either integer ID (e.g., 42) or alias string (e.g., 'api-design'). Returns memory object with id, name, alias, content, tags, age, timestamp, metadata (linesCount, sizeKB), parentMemoryId, and optional parent/children objects when requested. Returns status: 'not_found' if memory doesn't exist.")]
        public async Task<object> MemoryGet(
            [Description("Project name")] string project,
            [Description("The unique ID or alias of the memory to retrieve (e.g., 42 or 'api-design')")] string memoryIdOrAlias,
            [Description("Include child memories in response (default: true)")] bool includeChildren = true,
            [Description("Include parent memory in response (default: true)")] bool includeParent = true)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                var memory = await indexer.GetMemoryByIdOrAliasAsync(memoryIdOrAlias);
                
                if (memory == null)
                {
                    return new
                    {
                        status = "not_found",
                        message = $"Memory '{memoryIdOrAlias}' not found in project '{project}'"
                    };
                }
                
                // Get parent if requested and exists
                object? parent = null;
                if (includeParent && memory.ParentMemoryId.HasValue)
                {
                    var parentMemory = await indexer.GetMemoryAsync(memory.ParentMemoryId.Value);
                    if (parentMemory != null)
                    {
                        parent = new
                        {
                            id = parentMemory.Id,
                            name = parentMemory.MemoryName,
                            age = parentMemory.AgeDisplay,
                            tags = parentMemory.Tags
                        };
                    }
                }
                
                // Get children if requested
                object? children = null;
                if (includeChildren && memory.ChildMemoryIds.Any())
                {
                    var childMemories = await indexer.GetChildMemoriesAsync(memory.Id, 10);
                    children = childMemories.Select(c => new
                    {
                        id = c.Id,
                        name = c.MemoryName,
                        age = c.AgeDisplay,
                        tags = c.Tags
                    }).ToList();
                }
                
                // Build response with consistent structure
                var result = new
                {
                    status = "success",
                    memory = new
                    {
                        id = memory.Id,
                        name = memory.MemoryName,
                        alias = memory.Alias,
                        content = memory.FullDocumentText,
                        tags = memory.Tags,
                        age = memory.AgeDisplay,
                        timestamp = memory.Timestamp.ToString("O"),
                        metadata = new
                        {
                            linesCount = memory.LinesCount,
                            sizeKB = Math.Round(memory.SizeInKBytes, 2)
                        },
                        parentMemoryId = memory.ParentMemoryId,
                        parent = parent,
                        children = children
                    }
                };
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving memory");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Search memories using semantic similarity with advanced filtering
        /// </summary>
        [McpServerTool]
        [Description("Search memories using natural language queries with advanced filtering options. Returns top matches with similarity scores (0.0-1.0, higher = more similar), snippets, and metadata. Supports filtering by tags, date ranges, parent/child relationships, and score thresholds. Results are sorted by score (highest first).")]
        public async Task<object> MemorySearch(
            [Description("Project name to search within")] string project,
            [Description("Natural language search query (e.g., 'authentication flow', 'API design decisions')")] string query,
            [Description("Number of top results to return (default: 3)")] int topK = 3,
            [Description("Number of lines to include in each snippet (default: 10)")] int snippetLines = 10,
            [Description("Filter by tags (comma-separated, e.g., 'architecture,decisions')")] string? filterTags = null,
            [Description("Only show memories older than this many days")] int? olderThanDays = null,
            [Description("Filter by parent/child status: true=only parents, false=only leaf memories, null=all")] bool? hasChildren = null,
            [Description("Minimum similarity score threshold (0.0-1.0, default: 0.0)")] float minScore = 0.0f)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                
                var config = new MemorySearchConfig
                {
                    TopK = topK,
                    SnippetLineCount = snippetLines,
                    IncludeMetadata = true,
                    IncludeGraphRelations = false,
                    FilterTags = string.IsNullOrEmpty(filterTags) ? null : 
                                filterTags.Split(',').Select(t => t.Trim()).ToList(),
                    OlderThanDays = olderThanDays,
                    HasChildren = hasChildren,
                    MinScore = minScore
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
                            alias = r.Memory.Alias,
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
        [Description("List all memories stored in a project. Returns memory objects with id, name, tags, age, linesCount, sizeKB, hasParent (boolean), and childCount (number of direct children). Useful for understanding memory hierarchy and organization.")]
        public async Task<object> MemoryList(
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
                        alias = m.Alias,
                        tags = m.Tags,
                        age = m.AgeDisplay,
                        linesCount = m.LinesCount,
                        sizeKB = Math.Round(m.SizeInKBytes, 2),
                        hasParent = m.ParentMemoryId.HasValue,
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
        /// Append content to an existing memory as a child memory
        /// </summary>
        [McpServerTool]
        [Description("Append content to an existing memory as a child memory. When inheritParentTags=true (default), parent tags are automatically included. Additional tags can be provided and will be merged with inherited tags. Duplicate tags are automatically deduplicated.")]
        public async Task<object> MemoryAppend(
            [Description("Project name")] string project,
            [Description("The ID or alias of the parent memory to append to (e.g., 42 or 'api-design')")] string parentMemoryIdOrAlias,
            [Description("Descriptive name for the child memory")] string childMemoryName,
            [Description("Full text content of the child memory")] string content,
            [Description("Comma-separated tags for categorization (optional, inherits parent tags by default)")] string? tags = null,
            [Description("Whether to inherit tags from parent memory (default: true)")] bool inheritParentTags = true)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                
                // Get parent memory to inherit tags if needed
                var parentMemory = await indexer.GetMemoryByIdOrAliasAsync(parentMemoryIdOrAlias);
                if (parentMemory == null)
                {
                    return new
                    {
                        status = "error",
                        message = $"Parent memory '{parentMemoryIdOrAlias}' not found in project '{project}'"
                    };
                }
                
                // Build tag list
                var tagList = new List<string>();
                if (inheritParentTags && parentMemory.Tags.Any())
                {
                    tagList.AddRange(parentMemory.Tags);
                }
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
                
                // Create child memory
                var childMemory = new Memory
                {
                    ProjectName = project,
                    MemoryName = childMemoryName,
                    FullDocumentText = content,
                    Tags = tagList,
                    ParentMemoryId = parentMemory.Id
                };
                
                var stored = await indexer.AddMemoryAsync(childMemory);
                
                return new
                {
                    status = "success",
                    memoryId = stored.Id,
                    alias = stored.Alias,
                    parentMemoryId = parentMemory.Id,
                    message = $"Child memory '{childMemoryName}' with alias '{stored.Alias}' appended to parent memory '{parentMemoryIdOrAlias}' (ID: {parentMemory.Id})",
                    metadata = new
                    {
                        linesCount = stored.LinesCount,
                        sizeKB = Math.Round(stored.SizeInKBytes, 2),
                        tags = stored.Tags,
                        timestamp = stored.Timestamp.ToString("O"),
                        inheritedTags = inheritParentTags && parentMemory.Tags.Any()
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error appending to memory");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Get memory statistics for a project
        /// </summary>
        [McpServerTool]
        [Description("Get comprehensive memory system statistics including: memoryCount, vectorStats (vectorCount, dimension, memoryUsageMB, precision, searchMethod), contentStats (totalSizeKB, totalLines, averages), topTags (most frequent tags with counts), and graphStats (parent-child relationship counts).")]
        public async Task<object> MemoryGetStats(
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
                        memoriesWithParent = memories.Count(m => m.ParentMemoryId.HasValue),
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
        
        /// <summary>
        /// Import a markdown file as a memory hierarchy
        /// </summary>
        [McpServerTool]
        [Description("Import markdown file as memory hierarchy using headers. Headers (#, ##, ###, ####) create parent-child relationships. Each section becomes a memory with its content. Headers become memory names, content below headers becomes memory content.")]
        public async Task<object> MemoryImportMarkdown(
            [Description("Project name")] string project,
            [Description("Path to markdown file")] string filePath,
            [Description("Optional parent memory ID to attach imported hierarchy to")] int? parentMemoryId = null,
            [Description("Tags to add to all imported memories (comma-separated)")] string? tags = null)
        {
            try
            {
                filePath = PathConverter.ConvertPath(filePath);

                // Validate file exists
                if (!System.IO.File.Exists(filePath))
                {
                    return new
                    {
                        status = "error",
                        message = $"File not found: {filePath}"
                    };
                }
                
                // Read file content
                var content = await System.IO.File.ReadAllTextAsync(filePath);
                if (string.IsNullOrEmpty(content))
                {
                    return new
                    {
                        status = "error",
                        message = "File is empty"
                    };
                }
                
                var indexer = GetOrCreateMemoryIndexer(project);
                
                // Validate parent memory if provided
                if (parentMemoryId.HasValue)
                {
                    var parentMemory = await indexer.GetMemoryAsync(parentMemoryId.Value);
                    if (parentMemory == null)
                    {
                        return new
                        {
                            status = "error",
                            message = $"Parent memory {parentMemoryId.Value} not found"
                        };
                    }
                }
                
                // Parse tags
                var baseTags = string.IsNullOrEmpty(tags) ? new List<string>() : 
                               tags.Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                
                // Add file-based tags
                var fileName = System.IO.Path.GetFileName(filePath);
                var fileNameTag = $"imported-from-{System.IO.Path.GetFileNameWithoutExtension(fileName)}";
                if (!baseTags.Contains(fileNameTag, StringComparer.OrdinalIgnoreCase))
                {
                    baseTags.Add(fileNameTag);
                }
                baseTags.Add("imported-markdown");
                
                // Parse markdown structure
                var sections = ParseMarkdownSections(content);
                if (!sections.Any())
                {
                    return new
                    {
                        status = "error",
                        message = "No headers found in markdown file"
                    };
                }
                
                // Import sections as memory hierarchy
                var importedMemories = new List<(Memory memory, int level)>();
                var levelToMemoryId = new Dictionary<int, int>();
                
                if (parentMemoryId.HasValue)
                {
                    // Set the provided parent as level 0
                    levelToMemoryId[0] = parentMemoryId.Value;
                }
                
                foreach (var section in sections)
                {
                    // Determine parent ID based on header level
                    int? sectionParentId = null;
                    
                    // Look for the closest parent level
                    for (int parentLevel = section.Level - 1; parentLevel >= 0; parentLevel--)
                    {
                        if (levelToMemoryId.ContainsKey(parentLevel))
                        {
                            sectionParentId = levelToMemoryId[parentLevel];
                            break;
                        }
                    }
                    
                    // Create memory for this section
                    var memory = new Memory
                    {
                        ProjectName = project,
                        MemoryName = section.Title,
                        FullDocumentText = section.Content,
                        Tags = new List<string>(baseTags),
                        ParentMemoryId = sectionParentId
                    };
                    
                    // Add level-specific tag
                    memory.Tags.Add($"level-{section.Level}");
                    
                    // Store the memory
                    var stored = await indexer.AddMemoryAsync(memory);
                    
                    // Track this memory as potential parent for deeper levels
                    levelToMemoryId[section.Level] = stored.Id;
                    
                    importedMemories.Add((stored, section.Level));
                }
                
                // Generate import summary
                var summary = new
                {
                    status = "success",
                    message = $"Successfully imported {importedMemories.Count} memories from markdown file",
                    fileName = fileName,
                    importedCount = importedMemories.Count,
                    hierarchy = importedMemories.Select(im => new
                    {
                        id = im.memory.Id,
                        alias = im.memory.Alias,
                        name = im.memory.MemoryName,
                        level = im.level,
                        parentId = im.memory.ParentMemoryId,
                        sizeKB = Math.Round(im.memory.SizeInKBytes, 2),
                        lines = im.memory.LinesCount
                    }).ToList(),
                    totalSizeKB = Math.Round(importedMemories.Sum(im => im.memory.SizeInKBytes), 2),
                    tags = baseTags
                };
                
                return summary;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing markdown file");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Get memory tree structure with ASCII visualization
        /// </summary>
        [McpServerTool]
        [Description("Display memory tree structure with ASCII visualization. Shows hierarchical relationships between memories with IDs, aliases, names, and optionally content. Tree uses box-drawing characters for clear visualization.")]
        public async Task<object> MemoryGetTree(
            [Description("Project name")] string project,
            [Description("Root memory ID or alias (null = show all root memories)")] string? rootMemoryIdOrAlias = null,
            [Description("Maximum depth to display (default: 5)")] int maxDepth = 5,
            [Description("Include memory content preview in output (default: false)")] bool includeContent = false,
            [Description("Maximum content preview length in characters (default: 100)")] int contentPreviewLength = 100)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                var allMemories = await indexer.GetAllMemoriesAsync();
                
                if (!allMemories.Any())
                {
                    return new
                    {
                        status = "success",
                        message = "No memories found in project",
                        tree = ""
                    };
                }
                
                // Build memory lookup for efficient traversal
                var memoryLookup = allMemories.ToDictionary(m => m.Id);
                
                // Determine root memories
                List<Memory> rootMemories;
                if (!string.IsNullOrEmpty(rootMemoryIdOrAlias))
                {
                    // Find specific root memory
                    var rootMemory = await indexer.GetMemoryByIdOrAliasAsync(rootMemoryIdOrAlias);
                    if (rootMemory == null)
                    {
                        return new
                        {
                            status = "not_found",
                            message = $"Memory '{rootMemoryIdOrAlias}' not found in project '{project}'"
                        };
                    }
                    rootMemories = new List<Memory> { rootMemory };
                }
                else
                {
                    // Find all root memories (no parent)
                    rootMemories = allMemories.Where(m => !m.ParentMemoryId.HasValue).ToList();
                }
                
                if (!rootMemories.Any())
                {
                    return new
                    {
                        status = "success",
                        message = "No root memories found",
                        tree = ""
                    };
                }
                
                // Build tree visualization
                var treeBuilder = new System.Text.StringBuilder();
                var visitedIds = new HashSet<int>(); // Prevent circular references
                
                for (int i = 0; i < rootMemories.Count; i++)
                {
                    var isLastRoot = i == rootMemories.Count - 1;
                    BuildTreeNode(treeBuilder, rootMemories[i], memoryLookup, visitedIds, 
                                 "", isLastRoot, 0, maxDepth, includeContent, contentPreviewLength);
                }
                
                return new
                {
                    status = "success",
                    project = project,
                    rootCount = rootMemories.Count,
                    totalMemories = allMemories.Count,
                    tree = treeBuilder.ToString()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting memory tree");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Recursively build tree node visualization
        /// </summary>
        private void BuildTreeNode(
            System.Text.StringBuilder builder,
            Memory memory,
            Dictionary<int, Memory> memoryLookup,
            HashSet<int> visitedIds,
            string prefix,
            bool isLast,
            int currentDepth,
            int maxDepth,
            bool includeContent,
            int contentPreviewLength)
        {
            // Prevent circular references
            if (visitedIds.Contains(memory.Id))
            {
                builder.AppendLine($"{prefix}{(isLast ? "└── " : "├── ")}[CIRCULAR REFERENCE: #{memory.Id}]");
                return;
            }
            
            visitedIds.Add(memory.Id);
            
            // Build current node line
            var nodePrefix = isLast ? "└── " : "├── ";
            var nodeInfo = $"{memory.MemoryName} [#{memory.Id}]";
            if (!string.IsNullOrEmpty(memory.Alias))
            {
                nodeInfo += $" @{memory.Alias}";
            }
            
            builder.AppendLine($"{prefix}{nodePrefix}{nodeInfo}");
            
            // Add content preview if requested
            if (includeContent && !string.IsNullOrWhiteSpace(memory.FullDocumentText))
            {
                var childPrefix = prefix + (isLast ? "    " : "│   ");
                var contentPreview = memory.FullDocumentText.Length > contentPreviewLength
                    ? memory.FullDocumentText.Substring(0, contentPreviewLength) + "..."
                    : memory.FullDocumentText;
                
                // Clean up content for display
                contentPreview = contentPreview.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                contentPreview = System.Text.RegularExpressions.Regex.Replace(contentPreview, @"\s+", " ").Trim();
                
                builder.AppendLine($"{childPrefix}    \"{contentPreview}\"");
            }
            
            // Check depth limit
            if (currentDepth >= maxDepth)
            {
                if (memory.ChildMemoryIds.Any())
                {
                    var childPrefix = prefix + (isLast ? "    " : "│   ");
                    builder.AppendLine($"{childPrefix}└── ... ({memory.ChildMemoryIds.Count} more)");
                }
                return;
            }
            
            // Process children
            if (memory.ChildMemoryIds.Any())
            {
                var childPrefix = prefix + (isLast ? "    " : "│   ");
                var children = memory.ChildMemoryIds
                    .Where(id => memoryLookup.ContainsKey(id))
                    .Select(id => memoryLookup[id])
                    .OrderBy(m => m.MemoryName)
                    .ToList();
                
                for (int i = 0; i < children.Count; i++)
                {
                    var isLastChild = i == children.Count - 1;
                    BuildTreeNode(builder, children[i], memoryLookup, visitedIds,
                                 childPrefix, isLastChild, currentDepth + 1, maxDepth,
                                 includeContent, contentPreviewLength);
                }
            }
            
            visitedIds.Remove(memory.Id); // Allow revisiting in different branches
        }
        
        /// <summary>
        /// Export memory tree as structured markdown
        /// </summary>
        [McpServerTool]
        [Description("Export memory tree as structured markdown or other formats. Converts memory hierarchies into documentation with proper formatting. Supports markdown and JSON formats. Preserves hierarchy through header levels in markdown or nested structures in JSON.")]
        public async Task<object> MemoryExportTree(
            [Description("Project name")] string project,
            [Description("Root memory ID or alias (null = export all)")] string? rootMemoryIdOrAlias = null,
            [Description("Output format: markdown or json (default: markdown)")] string format = "markdown",
            [Description("Include full memory content (default: true)")] bool includeContent = true,
            [Description("Maximum depth to export (default: 10)")] int maxDepth = 10,
            [Description("Include metadata (timestamps, tags) in export (default: true)")] bool includeMetadata = true)
        {
            try
            {
                var indexer = GetOrCreateMemoryIndexer(project);
                var allMemories = await indexer.GetAllMemoriesAsync();
                
                if (!allMemories.Any())
                {
                    return new
                    {
                        status = "success",
                        message = "No memories found to export",
                        content = "",
                        format = format
                    };
                }
                
                // Build memory lookup
                var memoryLookup = allMemories.ToDictionary(m => m.Id);
                
                // Determine root memories
                List<Memory> rootMemories;
                if (!string.IsNullOrEmpty(rootMemoryIdOrAlias))
                {
                    var rootMemory = await indexer.GetMemoryByIdOrAliasAsync(rootMemoryIdOrAlias);
                    if (rootMemory == null)
                    {
                        return new
                        {
                            status = "not_found",
                            message = $"Memory '{rootMemoryIdOrAlias}' not found in project '{project}'"
                        };
                    }
                    rootMemories = new List<Memory> { rootMemory };
                }
                else
                {
                    rootMemories = allMemories.Where(m => !m.ParentMemoryId.HasValue).ToList();
                }
                
                if (!rootMemories.Any())
                {
                    return new
                    {
                        status = "success",
                        message = "No root memories found",
                        content = "",
                        format = format
                    };
                }
                
                // Export based on format
                string exportContent;
                switch (format.ToLower())
                {
                    case "json":
                        exportContent = ExportAsJson(rootMemories, memoryLookup, maxDepth, includeContent, includeMetadata);
                        break;
                    case "markdown":
                    default:
                        exportContent = ExportAsMarkdown(rootMemories, memoryLookup, maxDepth, includeContent, includeMetadata);
                        break;
                }
                
                return new
                {
                    status = "success",
                    format = format.ToLower(),
                    rootCount = rootMemories.Count,
                    totalExported = CountExportedMemories(rootMemories, memoryLookup, maxDepth),
                    contentLength = exportContent.Length,
                    content = exportContent
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting memory tree");
                return new { status = "error", message = ex.Message };
            }
        }
        
        /// <summary>
        /// Export memories as markdown document
        /// </summary>
        private string ExportAsMarkdown(List<Memory> rootMemories, Dictionary<int, Memory> memoryLookup, 
                                       int maxDepth, bool includeContent, bool includeMetadata)
        {
            var builder = new System.Text.StringBuilder();
            var visitedIds = new HashSet<int>();
            
            foreach (var root in rootMemories)
            {
                ExportMarkdownNode(builder, root, memoryLookup, visitedIds, 1, maxDepth, includeContent, includeMetadata);
                builder.AppendLine(); // Empty line between root trees
            }
            
            return builder.ToString().TrimEnd();
        }
        
        /// <summary>
        /// Recursively export a memory node as markdown
        /// </summary>
        private void ExportMarkdownNode(System.Text.StringBuilder builder, Memory memory, 
                                       Dictionary<int, Memory> memoryLookup, HashSet<int> visitedIds,
                                       int headerLevel, int maxDepth, bool includeContent, bool includeMetadata)
        {
            // Prevent circular references
            if (visitedIds.Contains(memory.Id))
            {
                builder.AppendLine($"{new string('#', headerLevel)} [Circular Reference: {memory.MemoryName}]");
                return;
            }
            
            visitedIds.Add(memory.Id);
            
            // Write header
            var headerPrefix = new string('#', Math.Min(headerLevel, 6)); // Markdown supports up to 6 levels
            builder.AppendLine($"{headerPrefix} {memory.MemoryName}");
            builder.AppendLine();
            
            // Add metadata if requested
            if (includeMetadata)
            {
                var metadata = new List<string>();
                if (!string.IsNullOrEmpty(memory.Alias))
                    metadata.Add($"Alias: @{memory.Alias}");
                if (memory.Tags.Any())
                    metadata.Add($"Tags: {string.Join(", ", memory.Tags)}");
                metadata.Add($"Created: {memory.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
                metadata.Add($"Size: {memory.SizeInKBytes:F2} KB");
                
                if (metadata.Any())
                {
                    builder.AppendLine($"*{string.Join(" | ", metadata)}*");
                    builder.AppendLine();
                }
            }
            
            // Add content if requested
            if (includeContent && !string.IsNullOrWhiteSpace(memory.FullDocumentText))
            {
                builder.AppendLine(memory.FullDocumentText);
                builder.AppendLine();
            }
            
            // Check depth limit
            if (headerLevel >= maxDepth)
            {
                if (memory.ChildMemoryIds.Any())
                {
                    builder.AppendLine($"*...{memory.ChildMemoryIds.Count} more child memories not shown (depth limit reached)*");
                    builder.AppendLine();
                }
                visitedIds.Remove(memory.Id);
                return;
            }
            
            // Process children
            if (memory.ChildMemoryIds.Any())
            {
                var children = memory.ChildMemoryIds
                    .Where(id => memoryLookup.ContainsKey(id))
                    .Select(id => memoryLookup[id])
                    .OrderBy(m => m.MemoryName)
                    .ToList();
                
                foreach (var child in children)
                {
                    ExportMarkdownNode(builder, child, memoryLookup, visitedIds, 
                                     headerLevel + 1, maxDepth, includeContent, includeMetadata);
                }
            }
            
            visitedIds.Remove(memory.Id);
        }
        
        /// <summary>
        /// Export memories as JSON
        /// </summary>
        private string ExportAsJson(List<Memory> rootMemories, Dictionary<int, Memory> memoryLookup,
                                   int maxDepth, bool includeContent, bool includeMetadata)
        {
            var visitedIds = new HashSet<int>();
            var roots = rootMemories.Select(root => 
                ExportJsonNode(root, memoryLookup, visitedIds, 0, maxDepth, includeContent, includeMetadata)
            ).ToList();
            
            var result = new
            {
                exportDate = DateTime.UtcNow.ToString("O"),
                totalMemories = roots.Count,
                memories = roots
            };
            
            return System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        }
        
        /// <summary>
        /// Recursively export a memory node as JSON object
        /// </summary>
        private object ExportJsonNode(Memory memory, Dictionary<int, Memory> memoryLookup, 
                                     HashSet<int> visitedIds, int currentDepth, int maxDepth,
                                     bool includeContent, bool includeMetadata)
        {
            if (visitedIds.Contains(memory.Id))
            {
                return new { circularReference = true, id = memory.Id, name = memory.MemoryName };
            }
            
            visitedIds.Add(memory.Id);
            
            var node = new Dictionary<string, object>
            {
                ["id"] = memory.Id,
                ["name"] = memory.MemoryName
            };
            
            if (!string.IsNullOrEmpty(memory.Alias))
                node["alias"] = memory.Alias;
            
            if (includeMetadata)
            {
                node["tags"] = memory.Tags;
                node["timestamp"] = memory.Timestamp.ToString("O");
                node["sizeKB"] = Math.Round(memory.SizeInKBytes, 2);
                node["lines"] = memory.LinesCount;
            }
            
            if (includeContent)
                node["content"] = memory.FullDocumentText;
            
            if (currentDepth < maxDepth && memory.ChildMemoryIds.Any())
            {
                var children = memory.ChildMemoryIds
                    .Where(id => memoryLookup.ContainsKey(id))
                    .Select(id => memoryLookup[id])
                    .OrderBy(m => m.MemoryName)
                    .Select(child => ExportJsonNode(child, memoryLookup, visitedIds, 
                                                   currentDepth + 1, maxDepth, includeContent, includeMetadata))
                    .ToList();
                
                if (children.Any())
                    node["children"] = children;
            }
            else if (memory.ChildMemoryIds.Any())
            {
                node["childrenOmitted"] = memory.ChildMemoryIds.Count;
            }
            
            visitedIds.Remove(memory.Id);
            return node;
        }
        
        /// <summary>
        /// Count total memories that will be exported
        /// </summary>
        private int CountExportedMemories(List<Memory> rootMemories, Dictionary<int, Memory> memoryLookup, int maxDepth)
        {
            var visitedIds = new HashSet<int>();
            var count = 0;
            
            foreach (var root in rootMemories)
            {
                count += CountMemoryNodes(root, memoryLookup, visitedIds, 0, maxDepth);
            }
            
            return count;
        }
        
        /// <summary>
        /// Recursively count memory nodes
        /// </summary>
        private int CountMemoryNodes(Memory memory, Dictionary<int, Memory> memoryLookup,
                                    HashSet<int> visitedIds, int currentDepth, int maxDepth)
        {
            if (visitedIds.Contains(memory.Id) || currentDepth > maxDepth)
                return 0;
            
            visitedIds.Add(memory.Id);
            var count = 1;
            
            if (currentDepth < maxDepth)
            {
                foreach (var childId in memory.ChildMemoryIds)
                {
                    if (memoryLookup.TryGetValue(childId, out var child))
                    {
                        count += CountMemoryNodes(child, memoryLookup, visitedIds, currentDepth + 1, maxDepth);
                    }
                }
            }
            
            return count;
        }
        
        /// <summary>
        /// Parse markdown content into sections based on headers
        /// </summary>
        private List<MarkdownSection> ParseMarkdownSections(string content)
        {
            var sections = new List<MarkdownSection>();
            var lines = content.Split('\n');
            
            MarkdownSection? currentSection = null;
            var contentBuilder = new System.Text.StringBuilder();
            
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmedLine = line.TrimStart();
                
                // Check if this is a header line
                if (trimmedLine.StartsWith("#"))
                {
                    // Save previous section if exists
                    if (currentSection != null)
                    {
                        currentSection.Content = contentBuilder.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(currentSection.Content))
                        {
                            sections.Add(currentSection);
                        }
                    }
                    
                    // Parse header level and title
                    var headerMatch = System.Text.RegularExpressions.Regex.Match(trimmedLine, @"^(#+)\s+(.+)$");
                    if (headerMatch.Success)
                    {
                        var level = headerMatch.Groups[1].Value.Length;
                        var title = headerMatch.Groups[2].Value.Trim();
                        
                        currentSection = new MarkdownSection
                        {
                            Level = level,
                            Title = title,
                            Content = ""
                        };
                        contentBuilder.Clear();
                    }
                }
                else if (currentSection != null)
                {
                    // Add non-header line to current section content
                    contentBuilder.AppendLine(line);
                }
            }
            
            // Save the last section
            if (currentSection != null)
            {
                currentSection.Content = contentBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(currentSection.Content))
                {
                    sections.Add(currentSection);
                }
            }
            
            return sections;
        }
        
        /// <summary>
        /// Represents a markdown section with header and content
        /// </summary>
        private class MarkdownSection
        {
            public int Level { get; set; }
            public string Title { get; set; } = string.Empty;
            public string Content { get; set; } = string.Empty;
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