using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace MailAggregator.Desktop.ViewModels;

public partial class MainViewModel
{
    private CancellationTokenSource? _aiCts;

    [ObservableProperty]
    private string? _aiMarkdown;

    [RelayCommand]
    private void OpenAiSettings()
    {
        var vm = App.Services.GetRequiredService<AiSettingsViewModel>();
        var window = new Views.AiSettingsWindow { DataContext = vm };
        window.ShowDialog();
    }

    [RelayCommand]
    private Task TranslateEmailAsync() => RunAiAsync(translate: true);

    [RelayCommand]
    private Task SummarizeEmailAsync() => RunAiAsync(translate: false);

    private async Task RunAiAsync(bool translate)
    {
        if (SelectedEmail == null)
        {
            StatusText = "Select an email first";
            return;
        }

        _aiCts?.Cancel();
        _aiCts?.Dispose();
        _aiCts = new CancellationTokenSource();
        var ct = _aiCts.Token;

        var label = translate ? "Translating" : "Summarizing";
        try
        {
            StatusText = $"{label} with AI...";
            IsSyncing = true;

            var aiService = App.Services.GetRequiredService<IAiService>();
            var markdown = translate
                ? await aiService.TranslateAsync(SelectedEmail, cancellationToken: ct)
                : await aiService.SummarizeAsync(SelectedEmail, cancellationToken: ct);

            ct.ThrowIfCancellationRequested();
            AiMarkdown = markdown;
            StatusText = translate ? "Translated" : "Summarized";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User triggered another AI action — silently abort
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "AI {Action} failed", translate ? "translate" : "summarize");
            StatusText = $"AI error: {ex.Message}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    partial void OnSelectedEmailChanging(EmailMessage? value)
    {
        // Switching emails resets any AI-rendered output. Clear the backing field
        // silently so the subsequent SelectedEmail PropertyChanged is what triggers
        // the re-render with the new email's body.
        _aiMarkdown = null;
    }
}
