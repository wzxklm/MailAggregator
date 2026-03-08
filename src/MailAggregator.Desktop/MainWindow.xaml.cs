using System.Windows;
using System.Windows.Controls;
using MailAggregator.Desktop.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace MailAggregator.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _webViewInitialized;

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

            // Block external resource loading by default (anti-tracking)
            EmailWebView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
            EmailWebView.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                var uri = args.Request.Uri;
                if (uri.StartsWith("http://") || uri.StartsWith("https://"))
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
            var email = _viewModel.SelectedEmail;
            UpdateEmailPreview(email?.BodyHtml, email?.BodyText);
        }
    }

    private void UpdateEmailPreview(string? htmlContent, string? textContent)
    {
        if (!_webViewInitialized) return;

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
}
