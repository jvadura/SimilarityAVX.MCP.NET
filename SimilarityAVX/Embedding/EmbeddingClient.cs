using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CSharpMcpServer.Models;
using CSharpMcpServer.Cache;
using Microsoft.Extensions.Options;
using VoyageAI;
using VoyageAI.Configuration;

namespace CSharpMcpServer.Embedding;

/// <summary>
/// EmbeddingClient with SQLite cache support and provider abstraction
/// </summary>
public class EmbeddingClient
{
    private readonly IEmbeddingProvider _provider;
    private readonly int _dimension;
    private readonly VectorPrecision _precision;
    private readonly IEmbeddingCacheRepository? _cache;
    
    public EmbeddingClient(EmbeddingConfig config, IEmbeddingCacheRepository? cache = null)
    {
        _dimension = config.Dimension;
        _precision = config.Precision;
        _cache = cache;
        
        // Create the appropriate provider based on configuration
        _provider = config.Provider switch
        {
            EmbeddingProvider.VoyageAI => CreateVoyageAIProvider(config),
            EmbeddingProvider.OpenAI => CreateOpenAIProvider(config),
            _ => throw new NotSupportedException($"Embedding provider {config.Provider} is not supported")
        };
        
        Console.Error.WriteLine($"[EmbeddingClient] Initialized with {config.Provider} provider, dim={_dimension}, precision={_precision}, cache={cache != null}");
    }
    
    private IEmbeddingProvider CreateVoyageAIProvider(EmbeddingConfig config)
    {
        var voyageOptions = new VoyageAIOptions
        {
            ApiKey = config.ApiKey,
            BaseUrl = config.ApiUrl,
            DefaultModel = config.Model,
            DefaultOutputDimension = config.Dimension,
            DefaultTruncation = true,
            DefaultOutputDtype = "float",
            Timeout = TimeSpan.FromMinutes(5),
            MaxRetryAttempts = config.MaxRetries,
            InitialRetryDelay = TimeSpan.FromMilliseconds(config.RetryDelayMs)
        };
        
        var httpClient = new HttpClient();
        var voyageClient = new VoyageAIClient(httpClient, Options.Create(voyageOptions));
        return new VoyageAIEmbeddingProvider(voyageClient, config);
    }
    
    private IEmbeddingProvider CreateOpenAIProvider(EmbeddingConfig config)
    {
        var httpClient = new HttpClient();
        return new OpenAIEmbeddingProvider(httpClient, config);
    }
    
    public async Task<byte[]> GetEmbeddingAsync(string text)
    {
        var embeddings = await GetEmbeddingsAsync(new[] { text });
        return embeddings[0];
    }
    
    public async Task<byte[]> GetQueryEmbeddingAsync(string query)
    {
        var embeddings = await GetEmbeddingsInternalAsync(new[] { query }, isQuery: true);
        return embeddings[0];
    }
    
    public async Task<byte[][]> GetEmbeddingsAsync(string[] texts)
    {
        return await GetEmbeddingsInternalAsync(texts, isQuery: false);
    }

    private async Task<byte[][]> GetEmbeddingsInternalAsync(string[] texts, bool isQuery)
    {
        var results = new byte[texts.Length][];
        var uncachedIndices = new List<int>();
        var uncachedTexts = new List<string>();
        
        // Check cache if available
        if (_cache != null)
        {
            for (int i = 0; i < texts.Length; i++)
            {
                var hash = SqliteEmbeddingCache.ComputeHash(texts[i]);
                var cached = await _cache.GetEmbeddingAsync(hash, isQuery);
                
                if (cached != null)
                {
                    results[i] = cached;
                }
                else
                {
                    uncachedIndices.Add(i);
                    uncachedTexts.Add(texts[i]);
                }
            }
            
            if (uncachedTexts.Count == 0)
            {
                Console.Error.WriteLine($"[EmbeddingClient] All {texts.Length} {(isQuery ? "query" : "document")} embeddings from cache");
                return results;
            }
            
            Console.Error.WriteLine($"[EmbeddingClient] {texts.Length - uncachedTexts.Count} cached, {uncachedTexts.Count} to compute");
        }
        else
        {
            // No cache, process all
            uncachedTexts = texts.ToList();
            for (int i = 0; i < texts.Length; i++)
            {
                uncachedIndices.Add(i);
            }
        }
        
        // Get embeddings for uncached texts
        if (uncachedTexts.Count > 0)
        {
            var newEmbeddings = await GetEmbeddingsFromApiAsync(uncachedTexts.ToArray(), isQuery);
            
            // Store results and update cache
            for (int i = 0; i < uncachedIndices.Count; i++)
            {
                var originalIndex = uncachedIndices[i];
                var embedding = newEmbeddings[i];
                results[originalIndex] = embedding;
                
                // Cache the new embedding
                if (_cache != null)
                {
                    var hash = SqliteEmbeddingCache.ComputeHash(texts[originalIndex]);
                    await _cache.StoreEmbeddingAsync(hash, embedding, isQuery);
                }
            }
        }
        
        return results;
    }

    private async Task<byte[][]> GetEmbeddingsFromApiAsync(string[] texts, bool isQuery)
    {
        float[][] embeddings;
        
        if (isQuery && texts.Length == 1)
        {
            var embedding = await _provider.GetQueryEmbeddingAsync(texts[0]);
            embeddings = new[] { embedding };
        }
        else
        {
            embeddings = await _provider.GetDocumentEmbeddingsAsync(texts);
        }
        
        // Convert float arrays to byte arrays based on precision
        var results = new byte[embeddings.Length][];
        for (int i = 0; i < embeddings.Length; i++)
        {
            if (embeddings[i].Length != _dimension)
            {
                throw new Exception($"Expected embedding dimension {_dimension}, but got {embeddings[i].Length}");
            }
            results[i] = ConvertToBytes(embeddings[i]);
        }
        
        return results;
    }
    
    private byte[] ConvertToBytes(float[] embedding)
    {
        if (_precision == VectorPrecision.Half)
        {
            var halfArray = new Half[embedding.Length];
            for (int i = 0; i < embedding.Length; i++)
            {
                halfArray[i] = (Half)embedding[i];
            }
            
            var halfSpan = MemoryMarshal.Cast<Half, byte>(halfArray);
            return halfSpan.ToArray();
        }
        else
        {
            var bytes = new byte[embedding.Length * sizeof(float)];
            Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}

/// <summary>
/// Exception thrown by embedding API with retry information
/// </summary>
public class EmbeddingApiException : Exception
{
    public bool IsRetryable { get; }
    
    public EmbeddingApiException(string message, bool isRetryable) : base(message)
    {
        IsRetryable = isRetryable;
    }
}