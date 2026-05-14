using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.Ai;

public interface IAiService
{
    Task<string> TranslateAsync(EmailMessage message, string? languageOverride = null, CancellationToken cancellationToken = default);
    Task<string> SummarizeAsync(EmailMessage message, string? languageOverride = null, CancellationToken cancellationToken = default);
}
