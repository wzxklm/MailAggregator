namespace MailAggregator.Core.Models;

public class AiSettings
{
    public int Id { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string EncryptedApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string DefaultLanguage { get; set; } = "English";
    public string TranslatePrompt { get; set; } = string.Empty;
    public string SummarizePrompt { get; set; } = string.Empty;
}
