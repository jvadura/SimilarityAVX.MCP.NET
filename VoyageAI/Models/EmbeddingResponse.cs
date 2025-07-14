using System.Text.Json.Serialization;

namespace VoyageAI.Models;

public class EmbeddingResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = null!;

    [JsonPropertyName("data")]
    public List<EmbeddingData> Data { get; set; } = null!;

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("usage")]
    public UsageInfo Usage { get; set; } = null!;
}

public class EmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = null!;

    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = null!;

    [JsonPropertyName("index")]
    public int Index { get; set; }
}

public class UsageInfo
{
    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}