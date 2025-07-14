using System.Threading.Tasks;

namespace CSharpMcpServer.Embedding;

/// <summary>
/// Interface for embedding providers (OpenAI, VoyageAI, etc.)
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Get embeddings for documents (for indexing)
    /// </summary>
    Task<float[][]> GetDocumentEmbeddingsAsync(string[] texts);
    
    /// <summary>
    /// Get embedding for a query (for searching)
    /// </summary>
    Task<float[]> GetQueryEmbeddingAsync(string query);
}