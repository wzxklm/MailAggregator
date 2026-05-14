using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Services.Ai;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MailAggregator.Desktop.ViewModels;

public partial class AiSettingsViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private bool _apiKeyChanged;

    [ObservableProperty]
    private string _baseUrl = string.Empty;

    [ObservableProperty]
    private string _model = string.Empty;

    [ObservableProperty]
    private string _defaultLanguage = "English";

    [ObservableProperty]
    private string _translatePrompt = string.Empty;

    [ObservableProperty]
    private string _summarizePrompt = string.Empty;

    [ObservableProperty]
    private string _apiKeyPlaceholder = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public bool DialogResult { get; private set; }

    public event Action? CloseRequested;

    public AiSettingsViewModel(ILogger logger)
    {
        _logger = logger;
    }

    public void NotifyApiKeyChanged() => _apiKeyChanged = true;

    public async Task LoadAsync()
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<IAiSettingsService>();
            var settings = await settingsService.GetAsync();

            BaseUrl = settings.BaseUrl;
            Model = settings.Model;
            DefaultLanguage = settings.DefaultLanguage;
            TranslatePrompt = string.IsNullOrWhiteSpace(settings.TranslatePrompt)
                ? settingsService.GetDefaultTranslatePrompt()
                : settings.TranslatePrompt;
            SummarizePrompt = string.IsNullOrWhiteSpace(settings.SummarizePrompt)
                ? settingsService.GetDefaultSummarizePrompt()
                : settings.SummarizePrompt;

            ApiKeyPlaceholder = string.IsNullOrEmpty(settings.EncryptedApiKey)
                ? "(not set)"
                : "(saved — leave blank to keep current)";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load AI settings");
            StatusText = $"Error loading settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<IAiSettingsService>();
            var current = await settingsService.GetAsync();

            current.BaseUrl = (BaseUrl ?? string.Empty).Trim();
            current.Model = (Model ?? string.Empty).Trim();
            current.DefaultLanguage = string.IsNullOrWhiteSpace(DefaultLanguage) ? "English" : DefaultLanguage.Trim();
            current.TranslatePrompt = TranslatePrompt ?? string.Empty;
            current.SummarizePrompt = SummarizePrompt ?? string.Empty;

            // If user did not enter a new key, keep the existing encrypted one
            string apiKeyToPersist;
            if (_apiKeyChanged && !string.IsNullOrEmpty(ApiKey))
            {
                apiKeyToPersist = ApiKey;
            }
            else if (_apiKeyChanged && string.IsNullOrEmpty(ApiKey))
            {
                // User explicitly cleared the field — clear stored key
                apiKeyToPersist = string.Empty;
                current.EncryptedApiKey = string.Empty;
            }
            else
            {
                // Unchanged — preserve existing encrypted key by re-decrypting and re-encrypting in service
                apiKeyToPersist = settingsService.GetDecryptedApiKey(current);
            }

            await settingsService.SaveAsync(current, apiKeyToPersist);

            DialogResult = true;
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save AI settings");
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        using var scope = App.Services.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IAiSettingsService>();
        TranslatePrompt = settingsService.GetDefaultTranslatePrompt();
        SummarizePrompt = settingsService.GetDefaultSummarizePrompt();
    }
}
