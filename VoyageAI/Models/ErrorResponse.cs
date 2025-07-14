using System.Text.Json.Serialization;

namespace VoyageAI.Models;

public class ErrorResponse
{
    [JsonPropertyName("error")]
    public ErrorDetail Error { get; set; } = null!;
}

public class ErrorDetail
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = null!;

    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;
}