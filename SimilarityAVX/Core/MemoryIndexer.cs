using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CSharpMcpServer.Embedding;
using CSharpMcpServer.Models;
using CSharpMcpServer.Storage;

namespace CSharpMcpServer.Core
{
    /// <summary>
    /// Handles memory indexing and embedding generation
    /// </summary>
    public class MemoryIndexer : IDisposable
    {
        private readonly string _projectName;
        private readonly MemoryStorage _storage;
        private readonly MemoryVectorStore _vectorStore;
        private readonly EmbeddingClient _embeddingClient;
        private readonly Configuration _config;
        
        public MemoryIndexer(string projectName, Configuration config)
        {
            _projectName = projectName;
            _config = config;
            _storage = new MemoryStorage(projectName);
            _vectorStore = new MemoryVectorStore();
            
            // Create memory-specific embedding config
            var memoryEmbeddingConfig = CreateMemoryEmbeddingConfig(config);
            
            // Initialize embedding client (no caching for memories)
            _embeddingClient = new EmbeddingClient(memoryEmbeddingConfig);
            
            // Load existing vectors on startup
            Task.Run(async () => await LoadExistingVectorsAsync()).Wait();
        }
        
        private EmbeddingConfig CreateMemoryEmbeddingConfig(Configuration config)
        {
            // Start with a copy of the main embedding config
            var memoryConfig = new EmbeddingConfig
            {
                Provider = config.Memory.Embedding.Provider ?? config.Embedding.Provider,
                ApiUrl = config.Memory.Embedding.ApiUrl ?? config.Embedding.ApiUrl,
                ApiKey = config.Memory.Embedding.ApiKey ?? config.Embedding.ApiKey,
                Model = config.Memory.Embedding.Model, // Always use memory-specific model
                Dimension = config.Memory.Embedding.Dimension ?? GetDefaultDimensionForModel(config.Memory.Embedding.Model),
                Precision = config.Embedding.Precision,
                BatchSize = config.Embedding.BatchSize,
                MaxRetries = config.Embedding.MaxRetries,
                RetryDelayMs = config.Embedding.RetryDelayMs,
                QueryInstruction = config.Embedding.QueryInstruction
            };
            
            return memoryConfig;
        }
        
        private int GetDefaultDimensionForModel(string model)
        {
            return model.ToLower() switch
            {
                "voyage-3-large" => 2048,
                "voyage-3" => 2048,
                "voyage-code-3" => 2048,
                _ => 2048 // Default
            };
        }
        
        private async Task LoadExistingVectorsAsync()
        {
            var vectors = await _storage.GetAllVectorsAsync();
            if (vectors.Count > 0)
            {
                _vectorStore.LoadVectors(vectors);
                Console.WriteLine($"[MemoryIndexer] Loaded {vectors.Count} existing memory vectors for project '{_projectName}'");
            }
        }
        
        /// <summary>
        /// Add a new memory and generate its embedding
        /// </summary>
        public async Task<Memory> AddMemoryAsync(Memory memory)
        {
            // Store the memory
            var storedMemory = await _storage.AddMemoryAsync(memory);
            
            // Generate embedding for the full document
            var embeddingBytes = await _embeddingClient.GetEmbeddingAsync(memory.FullDocumentText);
            
            // Convert bytes to float array
            var embedding = new float[embeddingBytes.Length / sizeof(float)];
            Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
            
            // Create vector entry
            var vectorEntry = new MemoryVectorEntry
            {
                MemoryId = storedMemory.Id,
                ProjectName = _projectName,
                Content = memory.FullDocumentText,
                Embedding = embedding,
                Precision = VectorPrecision.Float32,
                IndexedAt = DateTime.UtcNow
            };
            
            // Store vector in database
            await _storage.StoreVectorAsync(vectorEntry);
            
            // Add to in-memory store
            _vectorStore.AddVector(vectorEntry);
            
            Console.WriteLine($"[MemoryIndexer] Added memory '{memory.MemoryName}' with embedding (dimension: {embedding.Length})");
            
            return storedMemory;
        }
        
        /// <summary>
        /// Update an existing memory
        /// </summary>
        public async Task<Memory?> UpdateMemoryAsync(int memoryId, string? newName, string? newContent, List<string>? newTags)
        {
            // Get the existing memory first
            var existingMemory = await _storage.GetMemoryAsync(memoryId);
            if (existingMemory == null)
                return null;
            
            // Update in storage
            var updated = await _storage.UpdateMemoryAsync(memoryId, newName, newContent, newTags);
            if (!updated)
                return null;
            
            // If content changed, update the embedding
            if (newContent != null && newContent != existingMemory.FullDocumentText)
            {
                // Generate new embedding
                var embeddingBytes = await _embeddingClient.GetEmbeddingAsync(newContent);
                
                // Convert bytes to float array
                var embedding = new float[embeddingBytes.Length / sizeof(float)];
                Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
                
                // Update vector in store
                _vectorStore.UpdateVector(memoryId, embedding);
                
                // Update vector in database
                var vectorEntry = new MemoryVectorEntry
                {
                    MemoryId = memoryId,
                    ProjectName = _projectName,
                    Content = newContent,
                    Embedding = embedding,
                    Precision = VectorPrecision.Float32
                };
                
                await _storage.UpdateVectorAsync(vectorEntry);
                
                Console.WriteLine($"[MemoryIndexer] Updated memory {memoryId} with new embedding (dimension: {embedding.Length})");
            }
            else if (newName != null || newTags != null)
            {
                Console.WriteLine($"[MemoryIndexer] Updated memory {memoryId} metadata");
            }
            
            // Return the updated memory
            return await _storage.GetMemoryAsync(memoryId);
        }
        
        /// <summary>
        /// Delete a memory and its vectors
        /// </summary>
        public async Task<bool> DeleteMemoryAsync(int memoryId)
        {
            // Remove from vector store
            _vectorStore.RemoveVector(memoryId);
            
            // Delete vector from database
            await _storage.DeleteVectorAsync(memoryId);
            
            // Delete memory from database
            var deleted = await _storage.DeleteMemoryAsync(memoryId);
            
            if (deleted)
            {
                Console.WriteLine($"[MemoryIndexer] Deleted memory {memoryId}");
            }
            
            return deleted;
        }
        
        /// <summary>
        /// Retrieve a memory by ID
        /// </summary>
        public async Task<Memory?> GetMemoryAsync(int memoryId)
        {
            return await _storage.GetMemoryAsync(memoryId);
        }

        /// <summary>
        /// Retrieve a memory by alias
        /// </summary>
        public async Task<Memory?> GetMemoryByAliasAsync(string alias)
        {
            return await _storage.GetMemoryByAliasAsync(alias);
        }

        /// <summary>
        /// Retrieve a memory by ID or alias
        /// </summary>
        public async Task<Memory?> GetMemoryByIdOrAliasAsync(string idOrAlias)
        {
            return await _storage.GetMemoryByIdOrAliasAsync(idOrAlias);
        }
        
        /// <summary>
        /// Get child memories for a parent memory
        /// </summary>
        public async Task<List<Memory>> GetChildMemoriesAsync(int parentMemoryId, int limit = 10)
        {
            return await _storage.GetChildMemoriesAsync(parentMemoryId, limit);
        }
        
        /// <summary>
        /// Search memories using semantic similarity
        /// </summary>
        public async Task<List<MemorySearchResult>> SearchMemoriesAsync(
            string query, 
            MemorySearchConfig? config = null)
        {
            config ??= new MemorySearchConfig();
            
            // Get query embedding
            var queryEmbeddingBytes = await _embeddingClient.GetQueryEmbeddingAsync(query);
            var queryEmbedding = new float[queryEmbeddingBytes.Length / sizeof(float)];
            Buffer.BlockCopy(queryEmbeddingBytes, 0, queryEmbedding, 0, queryEmbeddingBytes.Length);
            
            // Search vectors
            var searchResults = _vectorStore.Search(queryEmbedding, config.TopK);
            
            var results = new List<MemorySearchResult>();
            
            foreach (var (vector, score) in searchResults)
            {
                // Apply score threshold filter first (most efficient)
                if (score < config.MinScore) continue;
                
                // Get full memory
                var memory = await _storage.GetMemoryAsync(vector.MemoryId);
                if (memory == null) continue;
                
                // Apply tag filter
                if (config.FilterTags != null && config.FilterTags.Any())
                {
                    var hasMatchingTag = config.FilterTags.Any(filterTag => 
                        memory.Tags.Any(memoryTag => 
                            memoryTag.Equals(filterTag, StringComparison.OrdinalIgnoreCase)));
                    if (!hasMatchingTag) continue;
                }
                
                // Apply date filter
                if (config.OlderThanDays.HasValue)
                {
                    var cutoffDate = DateTime.UtcNow.AddDays(-config.OlderThanDays.Value);
                    if (memory.Timestamp > cutoffDate) continue;
                }
                
                // Apply parent/child filter
                if (config.HasChildren.HasValue)
                {
                    var hasChildren = memory.ChildMemoryIds.Any();
                    if (config.HasChildren.Value && !hasChildren) continue;
                    if (!config.HasChildren.Value && hasChildren) continue;
                }
                
                // Extract snippet (first N lines)
                var lines = memory.FullDocumentText.Split('\n');
                var snippetLines = lines.Take(config.SnippetLineCount).ToArray();
                var snippet = string.Join('\n', snippetLines);
                
                // Add graph relations if requested
                if (config.IncludeGraphRelations && memory.ParentMemoryId.HasValue)
                {
                    // Load parent memory info (just name, not full content)
                    var parentMemory = await _storage.GetMemoryAsync(memory.ParentMemoryId.Value);
                    if (parentMemory != null)
                    {
                        memory.ParentMemoryId = parentMemory.Id;
                        // Note: Parent name could be added to Memory model if needed
                    }
                    
                    // Load recent child memories
                    if (memory.ChildMemoryIds.Any())
                    {
                        var childMemories = await _storage.GetChildMemoriesAsync(memory.Id, limit: 10);
                        memory.ChildMemoryIds = childMemories.Select(c => c.Id).ToList();
                    }
                }
                
                results.Add(new MemorySearchResult
                {
                    Memory = memory,
                    Score = score,
                    SnippetText = snippet,
                    SnippetLineCount = snippetLines.Length
                });
            }
            
            return results;
        }
        
        /// <summary>
        /// Get all memories for the project
        /// </summary>
        public async Task<List<Memory>> GetAllMemoriesAsync()
        {
            return await _storage.GetAllMemoriesAsync();
        }
        
        /// <summary>
        /// Get memory statistics
        /// </summary>
        public MemorySystemStats GetMemoryStats()
        {
            return _vectorStore.GetMemoryStats();
        }
        
        public void Dispose()
        {
            _storage?.Dispose();
            // EmbeddingClient doesn't implement IDisposable
        }
    }
}