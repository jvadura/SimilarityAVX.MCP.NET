namespace VoyageAI.Configuration;

public class VoyageAIOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.voyageai.com/v1/";
    public string DefaultModel { get; set; } = "voyage-code-3";
    public bool DefaultTruncation { get; set; } = true;
    public int DefaultOutputDimension { get; set; } = 2048;
    public string DefaultOutputDtype { get; set; } = "float";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxRetryAttempts { get; set; } = 6;
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}