using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using VoyageAI.Configuration;
using VoyageAI.Exceptions;
using VoyageAI.Models;

namespace VoyageAI;

public interface IVoyageAIClient
{
    Task<EmbeddingResponse> GetEmbeddingsAsync(
        string input,
        string? model = null,
        bool? truncation = null,
        int? outputDimension = null,
        string? outputDtype = null,
        string? inputType = null,
        CancellationToken cancellationToken = default);

    Task<EmbeddingResponse> GetEmbeddingsAsync(
        string[] inputs,
        string? model = null,
        bool? truncation = null,
        int? outputDimension = null,
        string? outputDtype = null,
        string? inputType = null,
        CancellationToken cancellationToken = default);
}

public class VoyageAIClient : IVoyageAIClient
{
    private readonly HttpClient _httpClient;
    private readonly VoyageAIOptions _options;
    private readonly IAsyncPolicy<HttpResponseMessage> _retryPolicy;
    private readonly JsonSerializerOptions _jsonOptions;

    public VoyageAIClient(HttpClient httpClient, IOptions<VoyageAIOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ArgumentException("API key is required", nameof(options));

        ConfigureHttpClient();
        _retryPolicy = CreateRetryPolicy();
        _jsonOptions = CreateJsonOptions();
    }

    public Task<EmbeddingResponse> GetEmbeddingsAsync(
        string input,
        string? model = null,
        bool? truncation = null,
        int? outputDimension = null,
        string? outputDtype = null,
        string? inputType = null,
        CancellationToken cancellationToken = default)
    {
        return GetEmbeddingsInternalAsync(
            input,
            model,
            truncation,
            outputDimension,
            outputDtype,
            inputType,
            cancellationToken);
    }

    public Task<EmbeddingResponse> GetEmbeddingsAsync(
        string[] inputs,
        string? model = null,
        bool? truncation = null,
        int? outputDimension = null,
        string? outputDtype = null,
        string? inputType = null,
        CancellationToken cancellationToken = default)
    {
        if (inputs == null || inputs.Length == 0)
            throw new ArgumentException("At least one input is required", nameof(inputs));

        if (inputs.Length > 1000)
            throw new ArgumentException("Maximum 1000 inputs per request", nameof(inputs));

        return GetEmbeddingsInternalAsync(
            inputs,
            model,
            truncation,
            outputDimension,
            outputDtype,
            inputType,
            cancellationToken);
    }

    private async Task<EmbeddingResponse> GetEmbeddingsInternalAsync(
        object input,
        string? model,
        bool? truncation,
        int? outputDimension,
        string? outputDtype,
        string? inputType,
        CancellationToken cancellationToken)
    {
        var request = new EmbeddingRequest
        {
            Input = input,
            Model = model ?? _options.DefaultModel,
            Truncation = truncation ?? _options.DefaultTruncation,
            OutputDimension = outputDimension ?? _options.DefaultOutputDimension,
            OutputDtype = outputDtype ?? _options.DefaultOutputDtype,
            InputType = inputType
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _retryPolicy.ExecuteAsync(async () =>
            await _httpClient.PostAsync("embeddings", content, cancellationToken).ConfigureAwait(false));

        if (response.IsSuccessStatusCode)
        {
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EmbeddingResponse>(responseJson, _jsonOptions)
                ?? throw new VoyageAIException("Failed to deserialize response", (int)response.StatusCode, "deserialization_error");
        }

        await HandleErrorResponseAsync(response, cancellationToken).ConfigureAwait(false);
        throw new VoyageAIException("Unexpected error", (int)response.StatusCode, "unknown_error");
    }

    private void ConfigureHttpClient()
    {
        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.Timeout = _options.Timeout;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private IAsyncPolicy<HttpResponseMessage> CreateRetryPolicy()
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                _options.MaxRetryAttempts,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1) * _options.InitialRetryDelay.TotalSeconds),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    var statusCode = outcome.Result?.StatusCode;
                    Console.WriteLine($"Retry {retryCount} after {timespan} seconds (Status: {statusCode})");
                });
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    private async Task HandleErrorResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var errorJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ErrorResponse? errorResponse = null;

        try
        {
            errorResponse = JsonSerializer.Deserialize<ErrorResponse>(errorJson, _jsonOptions);
        }
        catch
        {
            // If we can't parse the error response, use the raw content
        }

        var message = errorResponse?.Error?.Message ?? errorJson;
        var errorType = errorResponse?.Error?.Type ?? "unknown_error";

        throw response.StatusCode switch
        {
            HttpStatusCode.BadRequest => new VoyageAIBadRequestException(message),
            HttpStatusCode.Unauthorized => new VoyageAIAuthenticationException(message),
            HttpStatusCode.TooManyRequests => new VoyageAIRateLimitException(message),
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                => new VoyageAIServerException(message, (int)response.StatusCode),
            _ => new VoyageAIException(message, (int)response.StatusCode, errorType)
        };
    }
}