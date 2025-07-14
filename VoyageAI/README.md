# VoyageAI .NET Client

A universal .NET 9 client library for the Voyage AI embedding API, optimized for code embeddings with built-in retry logic and dependency injection support.

## Features

- Full support for Voyage AI embedding API
- Configurable retry logic with exponential backoff
- Strong typing with request/response DTOs
- Dependency injection ready
- Custom exception types for proper error handling
- Support for both single and batch embeddings
- Configurable defaults matching your specifications

## Installation

Add the VoyageAI project reference to your solution:

```xml
<ProjectReference Include="..\VoyageAI\VoyageAI.csproj" />
```

## Quick Start

### Using Dependency Injection

```csharp
// In your Startup.cs or Program.cs
services.AddVoyageAI("your-api-key-here");

// Or with full configuration
services.AddVoyageAI(options =>
{
    options.ApiKey = "your-api-key-here";
    options.DefaultModel = "voyage-code-3";
    options.DefaultOutputDimension = 2048;
    options.DefaultTruncation = true;
});

// Usage
public class MyService
{
    private readonly IVoyageAIClient _voyageClient;
    
    public MyService(IVoyageAIClient voyageClient)
    {
        _voyageClient = voyageClient;
    }
    
    public async Task<float[]> GetEmbedding(string text)
    {
        var response = await _voyageClient.GetEmbeddingsAsync(text);
        return response.Data[0].Embedding;
    }
}
```

### Direct Usage

```csharp
var httpClient = new HttpClient();
var options = Options.Create(new VoyageAIOptions { ApiKey = "your-api-key" });
var client = new VoyageAIClient(httpClient, options);

var response = await client.GetEmbeddingsAsync("Your code here");
```

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| ApiKey | Required | Your Voyage AI API key |
| BaseUrl | https://api.voyageai.com/v1/ | API base URL |
| DefaultModel | voyage-code-3 | Default embedding model |
| DefaultTruncation | true | Auto-truncate long texts |
| DefaultOutputDimension | 2048 | Embedding vector size |
| DefaultOutputDtype | float | Output data type (fp32) |
| Timeout | 60 seconds | HTTP request timeout |
| MaxRetryAttempts | 6 | Maximum retry attempts |

## Error Handling

The client provides typed exceptions for different error scenarios:

```csharp
try
{
    var response = await client.GetEmbeddingsAsync(text);
}
catch (VoyageAIRateLimitException ex)
{
    // Handle rate limit (429)
}
catch (VoyageAIAuthenticationException ex)
{
    // Handle auth errors (401)
}
catch (VoyageAIBadRequestException ex)
{
    // Handle bad requests (400)
}
catch (VoyageAIServerException ex)
{
    // Handle server errors (5xx)
}
catch (VoyageAIException ex)
{
    // Handle other API errors
}
```

## Advanced Usage

### Batch Embeddings

```csharp
var texts = new[] { "code1", "code2", "code3" };
var response = await client.GetEmbeddingsAsync(
    texts,
    outputDimension: 1024,  // Use smaller dimension
    inputType: "document"    // Optimize for indexing
);
```

### Semantic Search

```csharp
// Generate query embedding
var queryResponse = await client.GetEmbeddingsAsync(
    "How to implement quicksort?",
    inputType: "query"
);

// Generate document embeddings
var docResponse = await client.GetEmbeddingsAsync(
    documentTexts,
    inputType: "document"
);
```

## Rate Limits

- Basic tier: 3M tokens/minute, 2K requests/minute
- Tier 2 ($100+): 6M tokens/minute, 4K requests/minute
- Tier 3 ($1000+): 9M tokens/minute, 6K requests/minute

The client automatically handles rate limiting with exponential backoff.

## Testing

To test the client, set your API key and run:

```csharp
services.AddVoyageAI(Environment.GetEnvironmentVariable("VOYAGE_API_KEY"));
```