using System.Windows;
using System.Windows.Controls;
using MailAggregator.Desktop.ViewModels;
using Markdig;
using Microsoft.Web.WebView2.Core;

namespace MailAggregator.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _webViewInitialized;
    private bool _allowExternalImages;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        FolderTreeView.SelectedItemChanged += FolderTreeView_SelectedItemChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!NotificationHelper.IsExitRequested)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Run WebView2 init and data load concurrently
        var webViewTask = InitializeWebViewAsync();
        var dataTask = _viewModel.InitializeAsync();
        await Task.WhenAll(webViewTask, dataTask);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await EmailWebView.EnsureCoreWebView2Async();
            EmailWebView.CoreWebView2.Settings.IsScriptEnabled = false;
            EmailWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            EmailWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            // Block external navigation — open in default browser instead
            EmailWebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (args.Uri != null && !args.Uri.StartsWith("data:") && !args.Uri.StartsWith("about:"))
                {
                    args.Cancel = true;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = args.Uri,
                        UseShellExecute = true
                    });
                }
            };

            // Block external resource loading by default (anti-tracking), allow when user clicks "Load Images"
            EmailWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
            EmailWebView.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                var uri = args.Request.Uri;
                if (!_allowExternalImages && (uri.StartsWith("http://") || uri.StartsWith("https://")))
                {
                    args.Response = EmailWebView.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 403, "Blocked", "");
                }
            };

            _webViewInitialized = true;
        }
        catch (Exception ex)
        {
            // WebView2 runtime may not be installed
            Serilog.Log.Warning(ex, "WebView2 initialization failed — HTML preview unavailable");
        }
    }

    private void FolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is AccountFolderNode node && !node.IsAccount)
        {
            _ = _viewModel.SelectFolderCommand.ExecuteAsync(node);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedEmail))
        {
            _allowExternalImages = false;
            RenderCurrentPreview();
        }
        else if (e.PropertyName == nameof(MainViewModel.AiMarkdown))
        {
            RenderCurrentPreview();
        }
    }

    private void RenderCurrentPreview()
    {
        if (!string.IsNullOrEmpty(_viewModel.AiMarkdown))
        {
            RenderMarkdown(_viewModel.AiMarkdown);
        }
        else
        {
            var email = _viewModel.SelectedEmail;
            UpdateEmailPreview(email?.BodyHtml, email?.BodyText);
        }
    }

    private void UpdateEmailPreview(string? htmlContent, string? textContent)
    {
        if (!_webViewInitialized) return;

        // Show notification bar when external images are blocked
        if (!_allowExternalImages)
        {
            RemoteImagesBar.Visibility = HasExternalImages(htmlContent)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(htmlContent))
        {
            EmailWebView.NavigateToString(htmlContent);
        }
        else if (!string.IsNullOrEmpty(textContent))
        {
            var safeText = System.Net.WebUtility.HtmlEncode(textContent);
            EmailWebView.NavigateToString($"<html><body><pre style=\"font-family:sans-serif;white-space:pre-wrap;\">{safeText}</pre></body></html>");
        }
        else
        {
            EmailWebView.NavigateToString("<html><body></body></html>");
        }
    }

    private void RenderMarkdown(string markdown)
    {
        if (!_webViewInitialized) return;
        RemoteImagesBar.Visibility = Visibility.Collapsed;

        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();
        var bodyHtml = Markdown.ToHtml(markdown, pipeline);
        var doc = $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><style>
                body { font-family: 'Segoe UI', sans-serif; font-size: 14px; line-height: 1.55; color: #1a1a1a; padding: 16px; }
                h1, h2, h3, h4 { margin-top: 1em; margin-bottom: 0.4em; }
                h1 { font-size: 1.6em; } h2 { font-size: 1.35em; } h3 { font-size: 1.15em; }
                p { margin: 0.6em 0; }
                ul, ol { padding-left: 1.4em; }
                li { margin: 0.2em 0; }
                code { background: #f3f3f3; padding: 1px 4px; border-radius: 3px; font-family: Consolas, monospace; font-size: 0.95em; }
                pre { background: #f7f7f7; padding: 10px; border-radius: 4px; overflow: auto; }
                pre code { background: transparent; padding: 0; }
                blockquote { border-left: 3px solid #0078D4; margin: 0.6em 0; padding: 2px 12px; color: #555; background: #f7faff; }
                table { border-collapse: collapse; margin: 0.6em 0; }
                th, td { border: 1px solid #d0d0d0; padding: 4px 8px; }
                hr { border: none; border-top: 1px solid #e0e0e0; margin: 1.2em 0; }
                a { color: #0078D4; }
            </style></head><body>{{bodyHtml}}</body></html>
            """;
        EmailWebView.NavigateToString(doc);
    }

    private void LoadRemoteImages_Click(object sender, RoutedEventArgs e)
    {
        _allowExternalImages = true;
        RemoteImagesBar.Visibility = Visibility.Collapsed;
        RenderCurrentPreview();
    }

    private static bool HasExternalImages(string? html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        return html.Contains("src=\"http://", StringComparison.OrdinalIgnoreCase)
            || html.Contains("src=\"https://", StringComparison.OrdinalIgnoreCase)
            || html.Contains("src='http://", StringComparison.OrdinalIgnoreCase)
            || html.Contains("src='https://", StringComparison.OrdinalIgnoreCase);
    }
}
