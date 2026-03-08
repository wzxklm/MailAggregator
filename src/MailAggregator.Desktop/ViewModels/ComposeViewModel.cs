using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Mail;
using Microsoft.Win32;
using Serilog;

namespace MailAggregator.Desktop.ViewModels;

public enum ComposeMode
{
    New,
    Reply,
    ReplyAll,
    Forward
}

public partial class ComposeViewModel : ObservableObject
{
    private readonly IEmailSendService _emailSendService;
    private readonly ILogger _logger;

    [ObservableProperty]
    private ObservableCollection<Account> _senderAccounts = [];

    [ObservableProperty]
    private Account? _selectedSender;

    [ObservableProperty]
    private string _to = string.Empty;

    [ObservableProperty]
    private string _cc = string.Empty;

    [ObservableProperty]
    private string _bcc = string.Empty;

    [ObservableProperty]
    private string _subject = string.Empty;

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _attachmentPaths = [];

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private ComposeMode _mode = ComposeMode.New;

    [ObservableProperty]
    private string _windowTitle = "New Message";

    private EmailMessage? _originalMessage;

    public event Action? CloseRequested;

    public ComposeViewModel(IEmailSendService emailSendService, ILogger logger)
    {
        _emailSendService = emailSendService;
        _logger = logger;
    }

    public void SetSenderAccounts(IEnumerable<Account> accounts)
    {
        SenderAccounts = new ObservableCollection<Account>(accounts);
        SelectedSender ??= SenderAccounts.FirstOrDefault();
    }

    public void PrepareReply(EmailMessage original, Account? senderAccount, ComposeMode mode)
    {
        _originalMessage = original;
        Mode = mode;

        if (senderAccount != null)
            SelectedSender = SenderAccounts.FirstOrDefault(a => a.Id == senderAccount.Id) ?? SenderAccounts.FirstOrDefault();

        switch (mode)
        {
            case ComposeMode.Reply:
                WindowTitle = $"Re: {original.Subject}";
                To = original.FromAddress;
                Subject = original.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true
                    ? original.Subject
                    : $"Re: {original.Subject}";
                Body = BuildQuotedBody(original);
                break;

            case ComposeMode.ReplyAll:
                WindowTitle = $"Re: {original.Subject}";
                To = original.FromAddress;
                Cc = original.CcAddresses ?? string.Empty;
                Subject = original.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true
                    ? original.Subject
                    : $"Re: {original.Subject}";
                Body = BuildQuotedBody(original);
                break;

            case ComposeMode.Forward:
                WindowTitle = $"Fwd: {original.Subject}";
                Subject = original.Subject?.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) == true
                    ? original.Subject
                    : $"Fwd: {original.Subject}";
                Body = BuildQuotedBody(original);
                break;
        }
    }

    private static string BuildQuotedBody(EmailMessage original)
    {
        return $"\n\n--- Original Message ---\nFrom: {original.FromAddress}\nDate: {original.DateSent:yyyy-MM-dd HH:mm}\nSubject: {original.Subject}\n\n{original.BodyText}";
    }

    [RelayCommand]
    private void AddAttachment()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
            {
                AttachmentPaths.Add(file);
            }
        }
    }

    [RelayCommand]
    private void RemoveAttachment(string? path)
    {
        if (path != null)
            AttachmentPaths.Remove(path);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (SelectedSender == null)
        {
            StatusText = "Please select a sender account";
            return;
        }

        if (string.IsNullOrWhiteSpace(To))
        {
            StatusText = "Please enter a recipient";
            return;
        }

        try
        {
            IsSending = true;
            StatusText = "Sending...";

            var attachments = AttachmentPaths.Count > 0 ? AttachmentPaths.ToList() as IReadOnlyList<string> : null;
            var ccValue = string.IsNullOrWhiteSpace(Cc) ? null : Cc;
            var bccValue = string.IsNullOrWhiteSpace(Bcc) ? null : Bcc;

            switch (Mode)
            {
                case ComposeMode.New:
                    await _emailSendService.SendAsync(
                        SelectedSender, To, ccValue, bccValue,
                        Subject, Body, false, attachments);
                    break;

                case ComposeMode.Reply when _originalMessage != null:
                    await _emailSendService.ReplyAsync(
                        SelectedSender, _originalMessage,
                        Body, false, attachments);
                    break;

                case ComposeMode.ReplyAll when _originalMessage != null:
                    await _emailSendService.ReplyAllAsync(
                        SelectedSender, _originalMessage,
                        Body, false, attachments);
                    break;

                case ComposeMode.Forward when _originalMessage != null:
                    await _emailSendService.ForwardAsync(
                        SelectedSender, _originalMessage,
                        To, ccValue, bccValue,
                        Body, false, attachments);
                    break;
            }

            StatusText = "Sent!";
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to send email");
            StatusText = $"Failed to send: {ex.Message}";
        }
        finally
        {
            IsSending = false;
        }
    }
}
