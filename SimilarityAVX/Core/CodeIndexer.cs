using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CSharpMcpServer.Models;
using CSharpMcpServer.Embedding;
using CSharpMcpServer.Storage;
using CSharpMcpServer.Cache;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CSharpMcpServer.Core;

public class CodeIndexer : IDisposable
{
    private readonly EmbeddingClient _embedding;
    private readonly IEmbeddingCacheRepository _cache;
    private readonly SqliteStorage _storage;
    private readonly RoslynParser _parser;
    private readonly CParser _cParser;
    private readonly FileSynchronizer _synchronizer;
    private readonly QueryExpander _queryExpander;
    private VectorMemoryStore _memoryStore;
    private readonly int _dimension;
    private readonly VectorPrecision _precision;
    private readonly int _batchSize;
    private readonly int _maxDegreeOfParallelism;
    private readonly string _projectName;
    
    public CodeIndexer(Configuration config, string? projectName = null)
    {
        _projectName = projectName ?? "default";
        _cache = new SqliteEmbeddingCache(config.Embedding.Model, _projectName, maxMemoryItems: 5000);
        _embedding = new EmbeddingClient(config.Embedding, _cache);
        _storage = new SqliteStorage(projectName);
        
        // Configure parsers with options from config
        _parser = new RoslynParser(config.Parser.IncludeFilePath, config.Parser.IncludeProjectContext, config.Parser.MaxChunkSize);
        _cParser = new CParser(config.Parser.MaxChunkSize, config.Parser.IncludeFilePath, config.Parser.IncludeProjectContext);
        
        _synchronizer = new FileSynchronizer(config.Performance.MaxDegreeOfParallelism);
        _queryExpander = new QueryExpander(enabled: true); // Can be made configurable later
        _dimension = config.Embedding.Dimension;
        _precision = config.Embedding.Precision;
        _batchSize = config.Embedding.BatchSize;
        _maxDegreeOfParallelism = config.Performance.MaxDegreeOfParallelism;
        
        Console.Error.WriteLine($"[CodeIndexer] Initializing with dimension={_dimension}, precision={_precision}, maxParallelism={config.Performance.MaxDegreeOfParallelism}");
        
        // Load existing data to memory
        _memoryStore = _storage.LoadToMemory(_dimension, _precision, config.Performance.MaxDegreeOfParallelism);
        
        // Save configuration metadata
        var metadata = _memoryStore.GetMetadata();
        _storage.SaveMetadata("dimension", _dimension.ToString());
        _storage.SaveMetadata("precision", _precision.ToString());
        _storage.SaveMetadata("cpu_capabilities", metadata.CpuCapabilities);
    }
    
    public async Task<IndexStats> IndexDirectoryAsync(
        string directory, 
        bool forceReindex = false,
        IProgress<IndexProgress>? progress = null,
        FileChanges? precomputedChanges = null)
    {
        var stopwatch = Stopwatch.StartNew();
        
        // DEBUG: Log who is calling this method to track unwanted reindexing
        var stackTrace = new System.Diagnostics.StackTrace();
        var callingMethod = stackTrace.GetFrame(1)?.GetMethod()?.Name ?? "Unknown";
        Console.Error.WriteLine($"[CodeIndexer] IndexDirectoryAsync called by: {callingMethod}, project={_projectName}, directory={directory}, forceReindex={forceReindex}");
        
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directory}");
        }
        
        progress?.Report(new IndexProgress { Phase = "Detecting changes", Current = 0, Total = 1 });
        
        // Store project directory in metadata for future use
        if (!string.IsNullOrEmpty(_projectName))
        {
            _storage.SaveMetadata("project_directory", directory);
        }
        
        // Get file changes
        FileChanges changes;
        
        if (forceReindex)
        {
            // Clear existing data when force reindexing (but preserve embedding cache!)
            Console.Error.WriteLine($"[CodeIndexer] Force reindex requested - clearing existing data for project '{_projectName}'");
            _storage.Clear();
            _memoryStore.Clear();
            _synchronizer.ClearState();
            // NOTE: Deliberately NOT clearing cache - embeddings should persist across runs!
            
            changes = GetAllCsFiles(directory);
        }
        else if (precomputedChanges != null)
        {
            // Use the changes already computed by ProjectMonitor
            changes = precomputedChanges;
            Console.Error.WriteLine($"[CodeIndexer] Using precomputed changes: +{changes.Added.Count} ~{changes.Modified.Count} -{changes.Removed.Count}");
        }
        else
        {
            // Fall back to computing changes ourselves
            changes = _synchronizer.GetChanges(directory, _projectName);
        }
        
        if (!changes.HasChanges && !forceReindex)
        {
            progress?.Report(new IndexProgress { Phase = "No changes detected", Current = 1, Total = 1 });
            Console.Error.WriteLine("[CodeIndexer] No changes detected, index is up to date");
            return new IndexStats(0, 0, 0, stopwatch.Elapsed);
        }
        
        // Remove deleted/modified files from store and track deleted IDs
        var deletedIds = new List<string>();
        foreach (var file in changes.Removed.Concat(changes.Modified))
        {
            var ids = _storage.DeleteByPath(file);
            deletedIds.AddRange(ids);
            
            // Also remove from memory store incrementally
            _memoryStore.RemoveVectorsByPath(file);
        }
        
        // Process new/modified files
        var filesToIndex = changes.Added.Concat(changes.Modified).ToList();
        var processedFiles = 0;
        var totalChunks = 0;
        var skippedFiles = 0;
        
        Console.Error.WriteLine($"[CodeIndexer] Processing {filesToIndex.Count} files...");
        
        progress?.Report(new IndexProgress 
        { 
            Phase = "Parsing files", 
            Current = 0, 
            Total = filesToIndex.Count 
        });
        
        // Parse all files first
        var allChunks = new List<(CodeChunk chunk, string filePath)>();
        
        foreach (var file in filesToIndex)
        {
            try
            {
                var chunks = GetParser(file).ParseFile(file);
                
                if (chunks == null || chunks.Count == 0)
                {
                    skippedFiles++;
                    continue;
                }
                
                foreach (var chunk in chunks)
                {
                    allChunks.Add((chunk, file));
                }
                
                processedFiles++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CodeIndexer] Error processing {file}: {ex.Message}");
                skippedFiles++;
            }
            
            progress?.Report(new IndexProgress 
            { 
                Phase = "Parsing files", 
                Current = processedFiles + skippedFiles, 
                Total = filesToIndex.Count 
            });
        }
        
        // Get embeddings in batches
        if (allChunks.Any())
        {
            progress?.Report(new IndexProgress 
            { 
                Phase = "Getting embeddings", 
                Current = 0, 
                Total = allChunks.Count 
            });
            
            var chunksToStore = new List<(string id, string path, int start, int end, string content, byte[] embedding, VectorPrecision precision, string chunkType)>();
            
            // Dynamic batching to respect token limits
            const int maxTokensPerBatch = 120000;
            const int avgCharsPerToken = 3; // More conservative estimate for code (lots of symbols)
            const double safetyMargin = 0.8; // Use only 80% of limit for safety
            const int maxCharsPerBatch = (int)(maxTokensPerBatch * avgCharsPerToken * safetyMargin); // ~288,000 chars
            
            int i = 0;
            while (i < allChunks.Count)
            {
                var batch = new List<(CodeChunk chunk, string filePath)>();
                int currentBatchChars = 0;
                
                // Build a batch that respects token limits
                while (i < allChunks.Count && batch.Count < _batchSize)
                {
                    var chunkChars = allChunks[i].chunk.Content.Length;
                    
                    // If adding this chunk would exceed limit, stop (unless batch is empty)
                    if (batch.Count > 0 && currentBatchChars + chunkChars > maxCharsPerBatch)
                    {
                        break;
                    }
                    
                    // If single chunk exceeds limit, it needs special handling
                    if (chunkChars > maxCharsPerBatch)
                    {
                        Console.Error.WriteLine($"[CodeIndexer] Warning: Chunk exceeds token limit ({chunkChars} chars). Processing individually.");
                        // Process this single large chunk alone
                        if (batch.Count > 0)
                        {
                            break; // Process current batch first
                        }
                        batch.Add(allChunks[i]);
                        i++;
                        break; // Process this single chunk
                    }
                    
                    batch.Add(allChunks[i]);
                    currentBatchChars += chunkChars;
                    i++;
                }
                
                if (batch.Count == 0)
                {
                    continue; // Safety check
                }
                
                var texts = batch.Select(c => c.chunk.Content).ToArray();
                
                // Log batch info for debugging
                if (batch.Count < _batchSize || currentBatchChars > maxCharsPerBatch * 0.8)
                {
                    Console.Error.WriteLine($"[CodeIndexer] Processing batch: {batch.Count} chunks, ~{currentBatchChars / 1000}K chars, ~{currentBatchChars / avgCharsPerToken} tokens");
                }
                
                try
                {
                    var embeddings = await _embedding.GetEmbeddingsAsync(texts);
                    
                    for (int j = 0; j < batch.Count; j++)
                    {
                        var (chunk, filePath) = batch[j];
#if DEBUG
                        if (chunk.ChunkType.Contains("-auth") || chunk.ChunkType.Contains("-security"))
                        {
                            Console.Error.WriteLine($"[DEBUG] CodeIndexer: Storing auth chunk - Type: '{chunk.ChunkType}', File: {Path.GetFileName(filePath)}");
                        }
#endif
                        
                        chunksToStore.Add((
                            chunk.Id,
                            filePath,
                            chunk.StartLine,
                            chunk.EndLine,
                            chunk.Content,
                            embeddings[j],
                            _precision,
                            chunk.ChunkType
                        ));
                    }
                    
                    totalChunks += batch.Count;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[CodeIndexer] Error getting embeddings for batch: {ex.Message}");
                }
                
                progress?.Report(new IndexProgress 
                { 
                    Phase = "Getting embeddings", 
                    Current = totalChunks, 
                    Total = allChunks.Count 
                });
            }
            
            // Save to storage and update memory incrementally
            if (chunksToStore.Any())
            {
                progress?.Report(new IndexProgress { Phase = "Saving to database", Current = 0, Total = 1 });
                _storage.SaveChunks(chunksToStore);
                
                progress?.Report(new IndexProgress { Phase = "Updating memory index", Current = 0, Total = 1 });
                
                // Get the newly saved chunks and add them to memory store incrementally
                var newChunkIds = chunksToStore.Select(c => c.id).ToList();
                var newChunks = _storage.GetChunksByIds(newChunkIds);
                
                var newEntries = newChunks.Select(chunk => 
                {
                    var lastModified = File.Exists(chunk.file_path) 
                        ? File.GetLastWriteTimeUtc(chunk.file_path) 
                        : DateTime.UtcNow;
                        
                    return new VectorEntry
                    {
                        Id = chunk.id,
                        FilePath = chunk.file_path,
                        StartLine = chunk.start_line,
                        EndLine = chunk.end_line,
                        Content = chunk.content,
                        EmbeddingBytes = chunk.embedding,
                        SourcePrecision = (VectorPrecision)chunk.precision,
                        ChunkType = chunk.chunk_type,
                        LastModified = lastModified
                    };
                }).ToList();
                
                // Incrementally add new vectors to memory
                _memoryStore.AppendVectors(newEntries);
                
                Console.Error.WriteLine($"[CodeIndexer] Incrementally added {newEntries.Count} vectors to memory");
            }
        }
        
        // Update synchronizer state
        _synchronizer.SaveState(directory, _projectName);
        
        var duration = stopwatch.Elapsed;
        var stats = new IndexStats(processedFiles, totalChunks, skippedFiles, duration, changes);
        
        Console.Error.WriteLine($"[CodeIndexer] Indexing complete: {processedFiles} files, {totalChunks} chunks in {duration.TotalSeconds:F1}s");
        
        return stats;
    }
    
    public async Task<SearchResult[]> SearchAsync(string query, int limit = 5, bool expandQuery = true)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }
        
        var stopwatch = Stopwatch.StartNew();
        
        // Expand query with synonyms if enabled
        var searchQuery = expandQuery ? _queryExpander.ExpandQuery(query) : query;
        
        // Get query embedding (with instruction if configured)
        var queryEmbeddingBytes = await _embedding.GetQueryEmbeddingAsync(searchQuery);
        
        // Convert to float array for search
        float[] queryVector;
        if (_precision == VectorPrecision.Half)
        {
            var halfSpan = MemoryMarshal.Cast<byte, Half>(queryEmbeddingBytes);
            queryVector = new float[_dimension];
            for (int i = 0; i < _dimension; i++)
            {
                // DEBUG: Check Half values before conversion
                // BREAKPOINT HERE: Verify halfSpan[i] is not NaN or Infinity
                if (Half.IsNaN(halfSpan[i]) || Half.IsInfinity(halfSpan[i]))
                {
                    // TODO: Log warning - query vector contains invalid Half value at index i
                }
                
                queryVector[i] = (float)halfSpan[i];
                
                // DEBUG: Check float values after conversion
                // BREAKPOINT HERE: Verify queryVector[i] is valid
                if (float.IsNaN(queryVector[i]) || float.IsInfinity(queryVector[i]))
                {
                    // TODO: Log warning - Half to float conversion produced invalid value at index i
                }
            }
        }
        else
        {
            queryVector = new float[_dimension];
            Buffer.BlockCopy(queryEmbeddingBytes, 0, queryVector, 0, queryEmbeddingBytes.Length);
        }
        
        // Search
        var results = _memoryStore.Search(queryVector, limit);
        
        var searchTime = stopwatch.Elapsed;
        Console.Error.WriteLine($"[CodeIndexer] Search completed in {searchTime.TotalMilliseconds:F1}ms, found {results.Length} results");
        
        return results;
    }
    
    public void Clear()
    {
        _storage.Clear();
        _memoryStore.Clear();
        _synchronizer.ClearState();
        // NOTE: Deliberately NOT clearing cache - embeddings should persist!
        Console.Error.WriteLine("[CodeIndexer] Index cleared (cache preserved)");
    }
    
    public IndexStatistics GetStats()
    {
        var memStats = _memoryStore.GetStats();
        var metadata = _memoryStore.GetMetadata();
        
        return new IndexStatistics
        {
            ChunkCount = memStats.ChunkCount,
            FileCount = memStats.FileCount,
            MemoryUsageMB = memStats.MemoryUsageMB,
            VectorDimension = _dimension,
            Precision = _precision,
            SearchMethod = metadata.CpuCapabilities
        };
    }
    
    private FileChanges GetAllCsFiles(string directory)
    {
        try
        {
            var csFiles = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories);
            var razorFiles = Directory.EnumerateFiles(directory, "*.razor", SearchOption.AllDirectories);
            var cshtmlFiles = Directory.EnumerateFiles(directory, "*.cshtml", SearchOption.AllDirectories);
            var cFiles = Directory.EnumerateFiles(directory, "*.c", SearchOption.AllDirectories);
            var hFiles = Directory.EnumerateFiles(directory, "*.h", SearchOption.AllDirectories);
            
            var files = csFiles.Concat(razorFiles).Concat(cshtmlFiles).Concat(cFiles).Concat(hFiles)
                .Where(f => !ShouldIgnore(f, directory))
                .ToList();
            
            var csCount = files.Count(f => f.EndsWith(".cs"));
            var razorCount = files.Count(f => f.EndsWith(".razor"));
            var cshtmlCount = files.Count(f => f.EndsWith(".cshtml"));
            var cCount = files.Count(f => f.EndsWith(".c"));
            var hCount = files.Count(f => f.EndsWith(".h"));
            
            Console.Error.WriteLine($"[CodeIndexer] Found {files.Count} files for full reindex (.cs: {csCount}, .razor: {razorCount}, .cshtml: {cshtmlCount}, .c: {cCount}, .h: {hCount})");
            
            return new FileChanges(files, new List<string>(), new List<string>());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeIndexer] Error scanning directory: {ex.Message}");
            return new FileChanges(new List<string>(), new List<string>(), new List<string>());
        }
    }
    
    private bool ShouldIgnore(string filePath, string baseDirectory)
    {
        var relativePath = Path.GetRelativePath(baseDirectory, filePath).Replace('\\', '/');
        
        var ignorePatterns = new[] 
        { 
            "bin/", "obj/", ".vs/", "packages/", "TestResults/",
            "node_modules/", ".git/", "dist/", "build/"
        };
        
        return ignorePatterns.Any(pattern => relativePath.Contains(pattern));
    }
    
    private dynamic GetParser(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".c" or ".h" => _cParser,
            ".cs" or ".razor" or ".cshtml" => _parser,
            _ => _parser // Default to C# parser
        };
    }

    public void Dispose()
    {
        (_cache as IDisposable)?.Dispose();
        // SqliteStorage doesn't implement IDisposable
    }
}