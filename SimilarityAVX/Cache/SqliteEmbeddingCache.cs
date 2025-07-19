using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CSharpMcpServer.Storage;

namespace CSharpMcpServer.Cache;

/// <summary>
/// Fast SQLite-based embedding cache with memory layer for hot data
/// </summary>
public class SqliteEmbeddingCache : IEmbeddingCacheRepository, IDisposable
{
    private readonly EmbeddingCacheStorage _storage;
    private readonly string _model;
    private readonly string? _projectName;
    
    // Memory cache for hot embeddings
    private readonly Dictionary<string, (byte[] embedding, DateTime lastAccess)> _memoryCache;
    private readonly int _maxMemoryItems;
    private readonly object _lock = new();

    public SqliteEmbeddingCache(string model, string? projectName = null, int maxMemoryItems = 5000)
    {
        _storage = new EmbeddingCacheStorage();
        _model = model;
        _projectName = projectName;
        _maxMemoryItems = maxMemoryItems;
        _memoryCache = new Dictionary<string, (byte[], DateTime)>(maxMemoryItems);
    }

    public async Task<byte[]?> GetEmbeddingAsync(string contentHash, bool isQuery = false)
    {
        // Check memory cache first
        lock (_lock)
        {
            if (_memoryCache.TryGetValue(MakeCacheKey(contentHash, isQuery), out var cached))
            {
                // Update last access time
                _memoryCache[MakeCacheKey(contentHash, isQuery)] = (cached.embedding, DateTime.UtcNow);
                return cached.embedding;
            }
        }

        // Check SQLite cache
        var embeddingType = isQuery ? "query" : "document";
        var embedding = await _storage.GetEmbeddingAsync(contentHash, embeddingType, _model, _projectName);
        
        if (embedding != null)
        {
            // Add to memory cache
            AddToMemoryCache(contentHash, embedding, isQuery);
        }

        return embedding;
    }

    public async Task StoreEmbeddingAsync(string contentHash, byte[] embedding, bool isQuery = false)
    {
        var embeddingType = isQuery ? "query" : "document";
        
        // Store in SQLite
        await _storage.StoreEmbeddingAsync(contentHash, embedding, embeddingType, _model, _projectName);
        
        // Add to memory cache
        AddToMemoryCache(contentHash, embedding, isQuery);
    }

    public async Task<(int totalEntries, long totalBytes, int queryEntries, int documentEntries)> GetStatsAsync()
    {
        var totalEntries = await _storage.GetCacheSizeAsync();
        var totalBytes = await _storage.GetCacheSizeBytesAsync();
        
        // For now, return simple stats (can enhance with specific counts later if needed)
        return (totalEntries, totalBytes, 0, 0);
    }

    public async Task<int> ClearOldEntriesAsync(int daysOld = 90)
    {
        // Clear from memory cache
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddDays(-daysOld);
            var keysToRemove = _memoryCache
                .Where(kvp => kvp.Value.lastAccess < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in keysToRemove)
            {
                _memoryCache.Remove(key);
            }
        }

        // Clear from SQLite
        return await _storage.ClearOldEntriesAsync(daysOld);
    }

    public async Task ClearProjectCacheAsync()
    {
        // DISABLED: Cache should NEVER be cleared to preserve API costs!
        // This method is intentionally disabled to prevent accidental cache clearing.
        Console.Error.WriteLine("[SqliteEmbeddingCache] ClearProjectCacheAsync called but DISABLED - cache preserved!");
        await Task.CompletedTask;
        
        /* ORIGINAL CODE - DISABLED:
        // Clear memory cache
        lock (_lock)
        {
            _memoryCache.Clear();
        }

        // Clear SQLite cache for project
        if (_projectName != null)
        {
            await _storage.ClearProjectCacheAsync(_projectName);
        }
        */
    }

    /// <summary>
    /// Compute SHA256 hash of content for cache key
    /// </summary>
    public static string ComputeHash(string content)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash)
            .Replace('/', '_')
            .Replace('+', '-')
            .TrimEnd('=');
    }

    private void AddToMemoryCache(string contentHash, byte[] embedding, bool isQuery)
    {
        lock (_lock)
        {
            var key = MakeCacheKey(contentHash, isQuery);
            
            // Evict oldest if at capacity
            if (_memoryCache.Count >= _maxMemoryItems && !_memoryCache.ContainsKey(key))
            {
                var oldest = _memoryCache
                    .OrderBy(kvp => kvp.Value.lastAccess)
                    .First();
                _memoryCache.Remove(oldest.Key);
            }

            _memoryCache[key] = (embedding, DateTime.UtcNow);
        }
    }

    private string MakeCacheKey(string contentHash, bool isQuery)
    {
        return $"{(isQuery ? "q" : "d")}:{contentHash}";
    }

    public void Dispose()
    {
        _storage?.Dispose();
    }
}