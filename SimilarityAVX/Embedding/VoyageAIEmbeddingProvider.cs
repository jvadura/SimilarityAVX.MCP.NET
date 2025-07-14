using System;
using System.Linq;
using System.Threading.Tasks;
using CSharpMcpServer.Models;
using Microsoft.Extensions.Options;
using VoyageAI;
using VoyageAI.Configuration;

namespace CSharpMcpServer.Embedding;

/// <summary>
/// VoyageAI embedding provider using the VoyageAI client library
/// </summary>
public class VoyageAIEmbeddingProvider : IEmbeddingProvider
{
    private readonly IVoyageAIClient _voyageClient;
    private readonly string _model;
    private readonly int _dimension;
    
    public VoyageAIEmbeddingProvider(IVoyageAIClient voyageClient, EmbeddingConfig config)
    {
        _voyageClient = voyageClient ?? throw new ArgumentNullException(nameof(voyageClient));
        _model = config.Model;
        _dimension = config.Dimension;
        
        Console.Error.WriteLine($"[VoyageAIProvider] Initialized: {_model}, dim={_dimension}");
    }
    
    public async Task<float[][]> GetDocumentEmbeddingsAsync(string[] texts)
    {
        var response = await _voyageClient.GetEmbeddingsAsync(
            texts,
            model: _model,
            outputDimension: _dimension,
            inputType: "document" // CRITICAL: Mark as documents for indexing
        );
        
        return response.Data.Select(d => d.Embedding).ToArray();
    }
    
    public async Task<float[]> GetQueryEmbeddingAsync(string query)
    {
        var response = await _voyageClient.GetEmbeddingsAsync(
            query,
            model: _model,
            outputDimension: _dimension,
            inputType: "query" // CRITICAL: Mark as query for searching
        );
        
        return response.Data[0].Embedding;
    }
}