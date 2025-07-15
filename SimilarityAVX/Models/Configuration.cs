using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpMcpServer.Models;

public class Configuration
{
    [JsonPropertyName("embedding")]
    public EmbeddingConfig Embedding { get; set; } = new();
    
    [JsonPropertyName("parser")]
    public ParserConfig Parser { get; set; } = new();
    
    [JsonPropertyName("performance")]
    public PerformanceConfig Performance { get; set; } = new();
    
    [JsonPropertyName("api")]
    public ApiConfig Api { get; set; } = new();
    
    [JsonPropertyName("monitoring")]
    public MonitoringConfig Monitoring { get; set; } = new();
    
    [JsonPropertyName("debug")]
    public DebugConfig Debug { get; set; } = new();
    
    [JsonPropertyName("security")]
    public SecurityConfig Security { get; set; } = new();
    
    /// <summary>
    /// Load configuration from JSON file with environment variable fallbacks
    /// </summary>
    public static Configuration Load(string? configPath = null)
    {
        var config = new Configuration();
        
        // Try to load from JSON file first
        configPath ??= Environment.GetEnvironmentVariable("CONFIG_FILE") ?? "config.json";
        
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                };
                var jsonConfig = JsonSerializer.Deserialize<Configuration>(json, options);
                if (jsonConfig != null)
                {
                    config = jsonConfig;
                    Console.Error.WriteLine($"[Config] Loaded from {configPath}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Config] Error loading {configPath}: {ex.Message}");
                Console.Error.WriteLine("[Config] Falling back to environment variables");
            }
        }
        else
        {
            Console.Error.WriteLine($"[Config] No config file found at {configPath}, using environment variables");
        }
        
        // Apply environment variable overrides
        config.ApplyEnvironmentOverrides();
        
        return config;
    }
    
    /// <summary>
    /// Apply environment variable overrides to configuration
    /// </summary>
    private void ApplyEnvironmentOverrides()
    {
        // Only override API key from environment variable
        var key = GetEnvVar("EMBEDDING_API_KEY");
        if (key is not null)
            Embedding.ApiKey = key;
    }
    
    /// <summary>
    /// Validate configuration and throw if invalid
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrEmpty(Embedding.ApiKey))
            throw new InvalidOperationException("EMBEDDING_API_KEY is required");
        if (Embedding.Dimension <= 0)
            throw new InvalidOperationException("EMBEDDING_DIMENSION must be positive");
        if (string.IsNullOrEmpty(Embedding.ApiUrl))
            throw new InvalidOperationException("EMBEDDING_API_URL is required");
            
        // FP16 is working but disabled until TensorPrimitives adds AVX-512 support
        if (Embedding.Precision == VectorPrecision.Half)
        {
            throw new InvalidOperationException(
                "Half precision (FP16) is currently disabled pending TensorPrimitives AVX-512 support. " +
                "Please use Float32 precision instead. " +
                "Set \"precision\": \"Float32\" in config.json or EMBEDDING_PRECISION=Float32");
        }
    }
    
    /// <summary>
    /// Save current configuration to JSON file
    /// </summary>
    public void Save(string configPath)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(configPath, json);
        Console.Error.WriteLine($"[Config] Saved to {configPath}");
    }
    
    private static string? GetEnvVar(string name) => Environment.GetEnvironmentVariable(name);
}

public class EmbeddingConfig
{
    [JsonPropertyName("provider")]
    public EmbeddingProvider Provider { get; set; } = EmbeddingProvider.VoyageAI;
    
    [JsonPropertyName("apiUrl")]
    public string ApiUrl { get; set; } = "https://api.voyageai.com/v1/";
    
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "";
    
    [JsonPropertyName("model")]
    public string Model { get; set; } = "voyage-code-3";
    
    [JsonPropertyName("dimension")]
    public int Dimension { get; set; } = 2048;
    
    [JsonPropertyName("precision")]
    public VectorPrecision Precision { get; set; } = VectorPrecision.Float32;
    
    [JsonPropertyName("batchSize")]
    public int BatchSize { get; set; } = 50;
    
    [JsonPropertyName("maxRetries")]
    public int MaxRetries { get; set; } = 3;
    
    [JsonPropertyName("retryDelayMs")]
    public int RetryDelayMs { get; set; } = 1000;
    
    [JsonPropertyName("queryInstruction")]
    public string? QueryInstruction { get; set; }
}

public class ParserConfig
{
    [JsonPropertyName("includeFilePath")]
    public bool IncludeFilePath { get; set; } = false;
    
    [JsonPropertyName("includeProjectContext")]
    public bool IncludeProjectContext { get; set; } = false;
    
    [JsonPropertyName("maxChunkSize")]
    public int MaxChunkSize { get; set; } = 2000;
    
    [JsonPropertyName("enableSlidingWindow")]
    public bool EnableSlidingWindow { get; set; } = true;
}

public class PerformanceConfig
{
    [JsonPropertyName("enableAvx512")]
    public AvxSetting EnableAvx512 { get; set; } = AvxSetting.Auto;
    
    [JsonPropertyName("memoryLimit")]
    public long MemoryLimitMB { get; set; } = 1024; // 1GB default
    
    [JsonPropertyName("maxDegreeOfParallelism")]
    public int MaxDegreeOfParallelism { get; set; } = 16; // Limit parallel threads to prevent spawning 1000s
}

public class ApiConfig
{
    [JsonPropertyName("enableRestApi")]
    public bool EnableRestApi { get; set; } = false;
    
    [JsonPropertyName("restApiPort")]
    public int RestApiPort { get; set; } = 8080;
    
    [JsonPropertyName("enableMcp")]
    public bool EnableMcp { get; set; } = true;
}

public class MonitoringConfig
{
    [JsonPropertyName("enableAutoReindex")]
    public bool EnableAutoReindex { get; set; } = true;
    
    [JsonPropertyName("verifyOnStartup")]
    public bool VerifyOnStartup { get; set; } = true;
    
    [JsonPropertyName("debounceDelaySeconds")]
    public int DebounceDelaySeconds { get; set; } = 60;
    
    [JsonPropertyName("enableFileWatching")]
    public bool EnableFileWatching { get; set; } = true;
}

public class DebugConfig
{
    [JsonPropertyName("enableFp16Debug")]
    public bool EnableFp16Debug { get; set; } = false;
    
    [JsonPropertyName("debugVectorDimension")]
    public int DebugVectorDimension { get; set; } = 32;
    
    [JsonPropertyName("debugVectorCount")]
    public int DebugVectorCount { get; set; } = 10;
    
    [JsonPropertyName("generateRandomVectors")]
    public bool GenerateRandomVectors { get; set; } = true;
}

public enum AvxSetting
{
    Auto,
    Enabled, 
    Disabled
}

public enum EmbeddingProvider
{
    OpenAI,
    VoyageAI
}

public class SecurityConfig
{
    [JsonPropertyName("allowedDirectories")]
    public List<string> AllowedDirectories { get; set; } = new() { "/mnt/e/" };
    
    [JsonPropertyName("enablePathValidation")]
    public bool EnablePathValidation { get; set; } = true;
}