using Microsoft.Extensions.DependencyInjection;
using VoyageAI;
using VoyageAI.Configuration;
using VoyageAI.Extensions;

namespace VoyageAI.Example;

public class ExampleUsage
{
    public static async Task SimpleExample()
    {
        // Setup dependency injection
        var services = new ServiceCollection();
        
        // Option 1: Configure with API key directly
        services.AddVoyageAI("your-api-key-here");
        
        // Option 2: Configure with options
        services.AddVoyageAI(options =>
        {
            options.ApiKey = "your-api-key-here";
            options.DefaultModel = "voyage-code-3";
            options.DefaultOutputDimension = 2048;
            options.DefaultTruncation = true;
            options.DefaultOutputDtype = "float";
            options.Timeout = TimeSpan.FromSeconds(60);
            options.MaxRetryAttempts = 6;
        });

        var serviceProvider = services.BuildServiceProvider();
        var voyageClient = serviceProvider.GetRequiredService<IVoyageAIClient>();

        // Example 1: Embed a single text
        var singleResponse = await voyageClient.GetEmbeddingsAsync(
            "def fibonacci(n): return n if n <= 1 else fibonacci(n-1) + fibonacci(n-2)");
        
        Console.WriteLine($"Tokens used: {singleResponse.Usage.TotalTokens}");
        Console.WriteLine($"Embedding dimension: {singleResponse.Data[0].Embedding.Length}");

        // Example 2: Embed multiple texts with custom parameters
        var texts = new[]
        {
            "public static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);",
            "function isPrime(n) { for(let i = 2; i <= Math.sqrt(n); i++) if(n % i === 0) return false; return n > 1; }",
            "SELECT * FROM users WHERE created_at > DATE_SUB(NOW(), INTERVAL 7 DAY);"
        };

        var batchResponse = await voyageClient.GetEmbeddingsAsync(
            texts,
            model: "voyage-code-3",
            outputDimension: 1024,  // Using smaller dimension
            inputType: "document");  // Specifying document type for indexing

        Console.WriteLine($"Batch embeddings generated: {batchResponse.Data.Count}");
        foreach (var embedding in batchResponse.Data)
        {
            Console.WriteLine($"Index {embedding.Index}: {embedding.Embedding.Length} dimensions");
        }

        // Example 3: Using for semantic search (query vs documents)
        var queryEmbedding = await voyageClient.GetEmbeddingsAsync(
            "How to calculate factorial recursively?",
            inputType: "query");

        // Compare with document embeddings (cosine similarity would be calculated here)
        Console.WriteLine("Query embedding generated for semantic search");
    }

    // Direct usage without DI
    public static async Task DirectUsageExample()
    {
        var httpClient = new HttpClient();
        var options = Microsoft.Extensions.Options.Options.Create(new VoyageAIOptions
        {
            ApiKey = "your-api-key-here"
        });

        var client = new VoyageAIClient(httpClient, options);
        
        var response = await client.GetEmbeddingsAsync("console.log('Hello, World!');");
        Console.WriteLine($"Embedding generated with {response.Data[0].Embedding.Length} dimensions");
    }
}