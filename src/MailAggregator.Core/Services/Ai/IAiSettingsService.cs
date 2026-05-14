using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.Ai;

public interface IAiSettingsService
{
    Task<AiSettings> GetAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AiSettings settings, string apiKeyPlaintext, CancellationToken cancellationToken = default);
    string GetDecryptedApiKey(AiSettings settings);
    string GetDefaultTranslatePrompt();
    string GetDefaultSummarizePrompt();
}
