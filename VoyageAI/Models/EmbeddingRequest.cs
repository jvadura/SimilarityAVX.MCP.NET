using System.Text.Json.Serialization;

namespace VoyageAI.Models;

public class EmbeddingRequest
{
    [JsonPropertyName("input")]
    public object Input { get; set; } = null!;

    [JsonPropertyName("model")]
    public string Model { get; set; } = "voyage-code-3";

    [JsonPropertyName("input_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputType { get; set; }

    [JsonPropertyName("truncation")]
    public bool Truncation { get; set; } = true;

    [JsonPropertyName("output_dimension")]
    public int OutputDimension { get; set; } = 2048;

    [JsonPropertyName("output_dtype")]
    public string OutputDtype { get; set; } = "float";
}