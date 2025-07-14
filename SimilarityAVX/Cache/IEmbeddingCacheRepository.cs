using System.Threading.Tasks;

namespace CSharpMcpServer.Cache;

/// <summary>
/// Simple interface for embedding cache. Keep it lean!
/// </summary>
public interface IEmbeddingCacheRepository
{
    /// <summary>
    /// Get a single embedding from cache
    /// </summary>
    Task<byte[]?> GetEmbeddingAsync(string contentHash, bool isQuery = false);
    
    /// <summary>
    /// Store a single embedding in cache
    /// </summary>
    Task StoreEmbeddingAsync(string contentHash, byte[] embedding, bool isQuery = false);
    
    /// <summary>
    /// Get cache statistics
    /// </summary>
    Task<(int totalEntries, long totalBytes, int queryEntries, int documentEntries)> GetStatsAsync();
    
    /// <summary>
    /// Clear old entries
    /// </summary>
    Task<int> ClearOldEntriesAsync(int daysOld = 90);
    
    /// <summary>
    /// Clear cache for a specific project
    /// </summary>
    Task ClearProjectCacheAsync();
}