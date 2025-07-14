using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using CSharpMcpServer.Models;

namespace CSharpMcpServer.Embedding;

/// <summary>
/// OpenAI-compatible embedding provider (OpenAI, Ollama, etc.)
/// </summary>
public class OpenAIEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _dimension;
    private readonly string? _queryInstruction;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    public OpenAIEmbeddingProvider(HttpClient httpClient, EmbeddingConfig config)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _baseUrl = config.ApiUrl.TrimEnd('/');
        _apiKey = SanitizeHeaderValue(config.ApiKey);
        _model = config.Model;
        _dimension = config.Dimension;
        _queryInstruction = config.QueryInstruction;
        _maxRetries = config.MaxRetries;
        _baseDelay = TimeSpan.FromMilliseconds(config.RetryDelayMs);
        
        _http.Timeout = TimeSpan.FromMinutes(5);
        
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", _apiKey);
        }
        
        Console.Error.WriteLine($"[OpenAIProvider] Initialized: {_model} @ {_baseUrl}, dim={_dimension}");
    }

    public async Task<float[][]> GetDocumentEmbeddingsAsync(string[] texts)
    {
        return await GetEmbeddingsFromApiAsync(texts);
    }

    public async Task<float[]> GetQueryEmbeddingAsync(string query)
    {
        // Apply query instruction if configured
        var processedQuery = !string.IsNullOrEmpty(_queryInstruction) 
            ? $"Instruct: {_queryInstruction}\nQuery: {query}"
            : query;
            
        var embeddings = await GetEmbeddingsFromApiAsync(new[] { processedQuery });
        return embeddings[0];
    }

    private async Task<float[][]> GetEmbeddingsFromApiAsync(string[] texts)
    {
        return await ExecuteWithRetryAsync(async () =>
        {
            var request = new
            {
                model = _model,
                input = texts
            };
            
            var jsonContent = JsonSerializer.Serialize(request);
            var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");
            
            var response = await _http.PostAsync($"{_baseUrl}/v1/embeddings", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                var shouldRetry = IsRetryableError(response.StatusCode);
                throw new EmbeddingApiException($"Embedding API error: {response.StatusCode} - {error}", shouldRetry);
            }
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var data = doc.RootElement.GetProperty("data").EnumerateArray().ToArray();
            var embeddings = new float[texts.Length][];
            
            for (int i = 0; i < texts.Length; i++)
            {
                embeddings[i] = data[i].GetProperty("embedding")
                    .EnumerateArray()
                    .Select(e => (float)e.GetDouble())
                    .ToArray();
                
                if (embeddings[i].Length != _dimension)
                {
                    throw new Exception($"Expected embedding dimension {_dimension}, but got {embeddings[i].Length}");
                }
            }
            
            return embeddings;
        });
    }
    
    private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
    {
        Exception? lastException = null;
        
        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (EmbeddingApiException ex) when (ex.IsRetryable && attempt < _maxRetries)
            {
                lastException = ex;
                var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
                Console.Error.WriteLine($"[OpenAIProvider] API error on attempt {attempt + 1}/{_maxRetries + 1}, retrying in {delay.TotalSeconds}s: {ex.Message}");
                await Task.Delay(delay);
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                lastException = ex;
                var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
                Console.Error.WriteLine($"[OpenAIProvider] Network error on attempt {attempt + 1}/{_maxRetries + 1}, retrying in {delay.TotalSeconds}s: {ex.Message}");
                await Task.Delay(delay);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException && attempt < _maxRetries)
            {
                lastException = ex;
                var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
                Console.Error.WriteLine($"[OpenAIProvider] Timeout on attempt {attempt + 1}/{_maxRetries + 1}, retrying in {delay.TotalSeconds}s");
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[OpenAIProvider] Non-retryable error: {ex.Message}");
                throw;
            }
        }
        
        throw lastException ?? new Exception("All retry attempts failed");
    }
    
    private static bool IsRetryableError(System.Net.HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            System.Net.HttpStatusCode.InternalServerError => true,
            System.Net.HttpStatusCode.BadGateway => true,
            System.Net.HttpStatusCode.ServiceUnavailable => true,
            System.Net.HttpStatusCode.GatewayTimeout => true,
            System.Net.HttpStatusCode.RequestTimeout => true,
            System.Net.HttpStatusCode.TooManyRequests => true,
            _ => false
        };
    }
    
    private static string SanitizeHeaderValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
            
        return value.Replace("\r\n", "")
                   .Replace("\r", "")
                   .Replace("\n", "")
                   .Trim();
    }
}