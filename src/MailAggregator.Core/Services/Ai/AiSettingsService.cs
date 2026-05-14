using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using Microsoft.EntityFrameworkCore;

namespace MailAggregator.Core.Services.Ai;

public class AiSettingsService : IAiSettingsService
{
    private const int SingletonId = 1;
    private const string DefaultTranslatePrompt =
        "You are a professional email translator. Translate the following email into {language}. " +
        "Preserve the original meaning, tone, and structure. Output the result as GitHub-flavored markdown.";
    private const string DefaultSummarizePrompt =
        "You are a professional email assistant. Summarize the following email in {language}. " +
        "Highlight the key points, action items, and any deadlines. Output the result as GitHub-flavored markdown.";

    private readonly IDbContextFactory<MailAggregatorDbContext> _contextFactory;
    private readonly ICredentialEncryptionService _encryption;

    public AiSettingsService(
        IDbContextFactory<MailAggregatorDbContext> contextFactory,
        ICredentialEncryptionService encryption)
    {
        _contextFactory = contextFactory;
        _encryption = encryption;
    }

    public async Task<AiSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.AiSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SingletonId, cancellationToken);

        return settings ?? new AiSettings
        {
            Id = SingletonId,
            DefaultLanguage = "English",
            TranslatePrompt = DefaultTranslatePrompt,
            SummarizePrompt = DefaultSummarizePrompt
        };
    }

    public async Task SaveAsync(AiSettings settings, string apiKeyPlaintext, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AiSettings.FirstOrDefaultAsync(s => s.Id == SingletonId, cancellationToken);

        var encryptedKey = string.IsNullOrEmpty(apiKeyPlaintext)
            ? string.Empty
            : _encryption.Encrypt(apiKeyPlaintext);

        if (existing == null)
        {
            db.AiSettings.Add(new AiSettings
            {
                Id = SingletonId,
                BaseUrl = settings.BaseUrl ?? string.Empty,
                EncryptedApiKey = encryptedKey,
                Model = settings.Model ?? string.Empty,
                DefaultLanguage = string.IsNullOrWhiteSpace(settings.DefaultLanguage) ? "English" : settings.DefaultLanguage,
                TranslatePrompt = settings.TranslatePrompt ?? string.Empty,
                SummarizePrompt = settings.SummarizePrompt ?? string.Empty
            });
        }
        else
        {
            existing.BaseUrl = settings.BaseUrl ?? string.Empty;
            existing.EncryptedApiKey = encryptedKey;
            existing.Model = settings.Model ?? string.Empty;
            existing.DefaultLanguage = string.IsNullOrWhiteSpace(settings.DefaultLanguage) ? "English" : settings.DefaultLanguage;
            existing.TranslatePrompt = settings.TranslatePrompt ?? string.Empty;
            existing.SummarizePrompt = settings.SummarizePrompt ?? string.Empty;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public string GetDecryptedApiKey(AiSettings settings)
    {
        if (string.IsNullOrEmpty(settings.EncryptedApiKey))
            return string.Empty;
        return _encryption.Decrypt(settings.EncryptedApiKey);
    }

    public string GetDefaultTranslatePrompt() => DefaultTranslatePrompt;
    public string GetDefaultSummarizePrompt() => DefaultSummarizePrompt;
}
