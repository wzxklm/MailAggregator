using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.AccountManagement;
using MailAggregator.Core.Services.Mail;
using MailAggregator.Core.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace MailAggregator.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAccountService _accountService;
    private readonly IEmailSyncService _emailSyncService;
    private readonly ISyncManager _syncManager;
    private readonly ILogger _logger;
    private CancellationTokenSource? _folderSwitchCts;

    [ObservableProperty]
    private ObservableCollection<AccountFolderNode> _folderTree = [];

    [ObservableProperty]
    private ObservableCollection<EmailMessage> _emails = [];

    [ObservableProperty]
    private EmailMessage? _selectedEmail;

    [ObservableProperty]
    private AccountFolderNode? _selectedFolder;

    [ObservableProperty]
    private Account? _selectedFilterAccount;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private ObservableCollection<Account> _accounts = [];

    public MainViewModel(
        IAccountService accountService,
        IEmailSyncService emailSyncService,
        ISyncManager syncManager,
        ILogger logger)
    {
        _accountService = accountService;
        _emailSyncService = emailSyncService;
        _syncManager = syncManager;
        _logger = logger;

        _syncManager.NewEmailsReceived += OnNewEmailsReceived;
    }

    public void Dispose()
    {
        _syncManager.NewEmailsReceived -= OnNewEmailsReceived;
        _folderSwitchCts?.Cancel();
        _folderSwitchCts?.Dispose();
    }

    public async Task InitializeAsync()
    {
        await LoadAccountsAsync();
    }

    private Account? FindAccountById(int accountId)
        => Accounts.FirstOrDefault(a => a.Id == accountId);

    [RelayCommand]
    private async Task LoadAccountsAsync()
    {
        try
        {
            StatusText = "Loading accounts...";
            IsSyncing = true;

            var accounts = await _accountService.GetAllAccountsAsync();
            Accounts = new ObservableCollection<Account>(accounts);

            // Sync folders for all accounts concurrently (per-account errors don't block others)
            var syncTasks = accounts.Select(async account =>
            {
                try
                {
                    var folders = await _emailSyncService.SyncFoldersAsync(account);
                    return (account, folders: (IReadOnlyList<MailFolder>?)folders);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to sync folders for {Email}", account.EmailAddress);
                    return (account, folders: null);
                }
            });
            var results = await Task.WhenAll(syncTasks);

            var newTree = new ObservableCollection<AccountFolderNode>();
            foreach (var (account, folders) in results)
            {
                var accountNode = new AccountFolderNode
                {
                    DisplayName = account.DisplayName ?? account.EmailAddress,
                    Account = account,
                    IsAccount = true
                };

                if (folders != null)
                {
                    foreach (var folder in folders.OrderBy(f => f.SpecialUse != SpecialFolderType.Inbox)
                                                  .ThenBy(f => f.Name))
                    {
                        accountNode.Children.Add(new AccountFolderNode
                        {
                            DisplayName = folder.Name,
                            Folder = folder,
                            Account = account
                        });
                    }
                }

                newTree.Add(accountNode);

                // Start background sync
                if (account.IsEnabled)
                {
                    _ = _syncManager.StartAccountSyncAsync(account).ContinueWith(t =>
                        _logger.Error(t.Exception, "Sync failed for {Email}", account.EmailAddress),
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            FolderTree = newTree;

            // Select first inbox by default
            var firstInbox = FolderTree
                .SelectMany(a => a.Children)
                .FirstOrDefault(f => f.Folder?.SpecialUse == SpecialFolderType.Inbox);
            if (firstInbox != null)
            {
                await SelectFolderAsync(firstInbox);
            }

            StatusText = $"{accounts.Count} account(s) loaded";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load accounts");
            StatusText = "Error loading accounts";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task SelectFolderAsync(AccountFolderNode? node)
    {
        if (node == null || node.IsAccount) return;

        // Cancel any previous folder load operation
        _folderSwitchCts?.Cancel();
        _folderSwitchCts?.Dispose();
        _folderSwitchCts = new CancellationTokenSource();
        var ct = _folderSwitchCts.Token;

        SelectedFolder = node;

        try
        {
            StatusText = $"Loading {node.DisplayName}...";
            IsSyncing = true;

            await _emailSyncService.SyncIncrementalAsync(node.Account!, node.Folder!, ct);
            ct.ThrowIfCancellationRequested();
            await LoadEmailsForCurrentViewAsync(ct);

            StatusText = $"{node.DisplayName} - {Emails.Count} message(s)";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User switched to another folder, silently abort
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load folder {FolderName}", node.DisplayName);
            StatusText = $"Error loading {node.DisplayName}";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task ShowUnifiedInboxAsync()
    {
        try
        {
            StatusText = "Loading unified inbox...";
            IsSyncing = true;

            SelectedFolder = null;
            SelectedFilterAccount = null;

            // Sync all inbox folders concurrently
            var inboxFolders = FolderTree
                .SelectMany(a => a.Children)
                .Where(f => f.Folder?.SpecialUse == SpecialFolderType.Inbox && f.Account != null)
                .ToList();

            // Sync sequentially per account, parallel across accounts
            var syncTasks = inboxFolders
                .GroupBy(f => f.Account!.Id)
                .Select(async group =>
                {
                    foreach (var f in group)
                    {
                        await _emailSyncService.SyncIncrementalAsync(f.Account!, f.Folder!);
                    }
                });
            await Task.WhenAll(syncTasks);

            await LoadEmailsForCurrentViewAsync();

            StatusText = $"Unified Inbox - {Emails.Count} message(s)";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load unified inbox");
            StatusText = "Error loading unified inbox";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task FilterByAccountAsync(Account? account)
    {
        SelectedFilterAccount = account;
        await LoadEmailsForCurrentViewAsync();
    }

    private async Task LoadEmailsForCurrentViewAsync(CancellationToken cancellationToken = default)
    {
        using var scope = App.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Core.Data.MailAggregatorDbContext>();

        IQueryable<EmailMessage> query = dbContext.Messages
            .OrderByDescending(m => m.DateSent);

        if (SelectedFolder?.Folder != null)
        {
            query = query.Where(m => m.FolderId == SelectedFolder.Folder.Id);
        }
        else
        {
            // Unified inbox mode: show all inbox folders
            var inboxFolderIds = FolderTree
                .SelectMany(a => a.Children)
                .Where(f => f.Folder?.SpecialUse == SpecialFolderType.Inbox)
                .Select(f => f.Folder!.Id)
                .ToList();
            query = query.Where(m => inboxFolderIds.Contains(m.FolderId));
        }

        if (SelectedFilterAccount != null)
        {
            query = query.Where(m => m.AccountId == SelectedFilterAccount.Id);
        }

        // Project only fields needed for the list view (exclude large body fields)
        var messages = await query
            .Select(m => new EmailMessage
            {
                Id = m.Id,
                AccountId = m.AccountId,
                FolderId = m.FolderId,
                Uid = m.Uid,
                MessageId = m.MessageId,
                InReplyTo = m.InReplyTo,
                References = m.References,
                FromAddress = m.FromAddress,
                FromName = m.FromName,
                ToAddresses = m.ToAddresses,
                CcAddresses = m.CcAddresses,
                Subject = m.Subject,
                DateSent = m.DateSent,
                PreviewText = m.PreviewText,
                IsRead = m.IsRead,
                HasAttachments = m.HasAttachments,
                CachedAt = m.CachedAt
            })
            .Take(200)
            .ToListAsync(cancellationToken);

        Emails = new ObservableCollection<EmailMessage>(messages);
    }

    [RelayCommand]
    private async Task MarkAsReadAsync(EmailMessage? message)
    {
        if (message == null || message.IsRead) return;

        try
        {
            var account = FindAccountById(message.AccountId);
            if (account == null) return;

            await _emailSyncService.SetMessageReadAsync(account, message, true);
            message.IsRead = true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to mark message as read");
        }
    }

    [RelayCommand]
    private async Task DeleteMessageAsync(EmailMessage? message)
    {
        if (message == null) return;

        try
        {
            var account = FindAccountById(message.AccountId);
            if (account == null) return;

            await _emailSyncService.DeleteMessageAsync(account, message);
            Emails.Remove(message);
            if (SelectedEmail == message) SelectedEmail = null;
            StatusText = "Message deleted";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete message");
            StatusText = "Error deleting message";
        }
    }

    [RelayCommand]
    private void ComposeNew()
    {
        if (Accounts.Count == 0)
        {
            StatusText = "Add an account first";
            return;
        }

        var vm = App.Services.GetRequiredService<ComposeViewModel>();
        vm.SetSenderAccounts(Accounts);
        var window = new Views.ComposeWindow { DataContext = vm };
        window.Show();
    }

    [RelayCommand]
    private void Reply() => OpenComposeForReply(ComposeMode.Reply);

    [RelayCommand]
    private void ReplyAll() => OpenComposeForReply(ComposeMode.ReplyAll);

    [RelayCommand]
    private void Forward() => OpenComposeForReply(ComposeMode.Forward);

    private void OpenComposeForReply(ComposeMode mode)
    {
        if (SelectedEmail == null) return;

        var vm = App.Services.GetRequiredService<ComposeViewModel>();
        vm.SetSenderAccounts(Accounts);
        var senderAccount = FindAccountById(SelectedEmail.AccountId);
        vm.PrepareReply(SelectedEmail, senderAccount, mode);
        var window = new Views.ComposeWindow { DataContext = vm };
        window.Show();
    }

    [RelayCommand]
    private void OpenAccountSettings()
    {
        var vm = App.Services.GetRequiredService<AccountListViewModel>();
        var window = new Views.AccountListWindow { DataContext = vm };
        window.ShowDialog();
        // Refresh after account changes
        _ = LoadAccountsAsync().ContinueWith(t =>
            _logger.Error(t.Exception, "Failed to reload accounts"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    partial void OnSelectedEmailChanged(EmailMessage? value)
    {
        if (value != null)
        {
            // Load the full message body for preview
            _ = LoadFullMessageAndMarkReadAsync(value);
        }
    }

    private async Task LoadFullMessageAndMarkReadAsync(EmailMessage listMessage)
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<Core.Data.MailAggregatorDbContext>();
            var fullMessage = await dbContext.Messages
                .Include(m => m.Attachments)
                .FirstOrDefaultAsync(m => m.Id == listMessage.Id);

            if (fullMessage != null)
            {
                // If body not cached yet, fetch from IMAP on demand
                if (fullMessage.BodyHtml == null && fullMessage.BodyText == null)
                {
                    var account = FindAccountById(listMessage.AccountId);
                    if (account != null)
                    {
                        await _emailSyncService.FetchMessageBodyAsync(account, fullMessage);
                    }
                }

                listMessage.BodyHtml = fullMessage.BodyHtml;
                listMessage.BodyText = fullMessage.BodyText;
                listMessage.Attachments = fullMessage.Attachments;

                OnPropertyChanged(nameof(SelectedEmail));
            }

            await MarkAsReadAsync(listMessage);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load full message");
        }
    }

    private void OnNewEmailsReceived(object? sender, NewEmailsEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = $"New mail from {e.AccountEmail} ({e.NewMessageCount} message(s))";

            _ = InsertNewEmailsAsync(e.AccountId).ContinueWith(t =>
                _logger.Error(t.Exception, "Failed to refresh email list"),
                TaskContinuationOptions.OnlyOnFaulted);

            NotificationHelper.ShowNewMailNotification(e.AccountEmail, e.NewMessageCount);
        });
    }

    private async Task InsertNewEmailsAsync(int accountId)
    {
        try
        {
            using var scope = App.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<Core.Data.MailAggregatorDbContext>();

            // Determine which folder IDs are currently displayed
            var visibleFolderIds = new List<int>();
            if (SelectedFolder?.Folder != null)
            {
                visibleFolderIds.Add(SelectedFolder.Folder.Id);
            }
            else
            {
                // Unified inbox mode
                visibleFolderIds = FolderTree
                    .SelectMany(a => a.Children)
                    .Where(f => f.Folder?.SpecialUse == SpecialFolderType.Inbox)
                    .Select(f => f.Folder!.Id)
                    .ToList();
            }

            if (visibleFolderIds.Count == 0) return;

            // Only fetch messages newer than what we already have
            var latestDate = Emails.FirstOrDefault()?.DateSent ?? DateTimeOffset.MinValue;

            var newMessages = await dbContext.Messages
                .Where(m => m.AccountId == accountId
                    && visibleFolderIds.Contains(m.FolderId)
                    && m.DateSent > latestDate)
                .OrderByDescending(m => m.DateSent)
                .Select(m => new EmailMessage
                {
                    Id = m.Id, AccountId = m.AccountId, FolderId = m.FolderId,
                    Uid = m.Uid, MessageId = m.MessageId, InReplyTo = m.InReplyTo,
                    References = m.References, FromAddress = m.FromAddress,
                    FromName = m.FromName, ToAddresses = m.ToAddresses,
                    CcAddresses = m.CcAddresses, Subject = m.Subject,
                    DateSent = m.DateSent, PreviewText = m.PreviewText,
                    IsRead = m.IsRead, HasAttachments = m.HasAttachments,
                    CachedAt = m.CachedAt
                })
                .ToListAsync();

            // Prepend new messages and replace collection in one notification
            if (newMessages.Count > 0)
            {
                Emails = new ObservableCollection<EmailMessage>(newMessages.Concat(Emails));
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Incremental email insert failed, falling back to full reload");
            await LoadEmailsForCurrentViewAsync();
        }
    }
}

public partial class AccountFolderNode : ObservableObject
{
    [ObservableProperty]
    private string _displayName = string.Empty;

    public Account? Account { get; set; }
    public MailFolder? Folder { get; set; }
    public bool IsAccount { get; set; }
    public ObservableCollection<AccountFolderNode> Children { get; } = [];
}
