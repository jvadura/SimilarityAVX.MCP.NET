using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using VoyageAI.Configuration;

namespace VoyageAI.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVoyageAI(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VoyageAIOptions>(configuration.GetSection("VoyageAI"));
        
        services.AddHttpClient<IVoyageAIClient, VoyageAIClient>()
            .AddPolicyHandler(GetRetryPolicy());

        return services;
    }

    public static IServiceCollection AddVoyageAI(this IServiceCollection services, Action<VoyageAIOptions> configureOptions)
    {
        services.Configure(configureOptions);
        
        services.AddHttpClient<IVoyageAIClient, VoyageAIClient>()
            .AddPolicyHandler(GetRetryPolicy());

        return services;
    }

    public static IServiceCollection AddVoyageAI(this IServiceCollection services, string apiKey)
    {
        services.Configure<VoyageAIOptions>(options =>
        {
            options.ApiKey = apiKey;
        });
        
        services.AddHttpClient<IVoyageAIClient, VoyageAIClient>()
            .AddPolicyHandler(GetRetryPolicy());

        return services;
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        return Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    // This is a simplified retry policy. The actual retry logic is in VoyageAIClient
                });
    }
}