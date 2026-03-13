using System.Collections.ObjectModel;
using System.IO;
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
using Serilog.Events;

namespace MailAggregator.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IAccountService _accountService;
    private readonly IEmailSyncService _emailSyncService;
    private readonly ISyncManager _syncManager;
    private readonly ILogger _logger;
    private CancellationTokenSource? _folderSwitchCts;
    private CancellationTokenSource? _loadAccountsCts;

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
    private string _logLevel = "INFO";

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
        _syncManager.FoldersSynced += OnFoldersSynced;
    }

    public void Dispose()
    {
        _syncManager.NewEmailsReceived -= OnNewEmailsReceived;
        _syncManager.FoldersSynced -= OnFoldersSynced;
        _folderSwitchCts?.Cancel();
        _folderSwitchCts?.Dispose();
        _loadAccountsCts?.Cancel();
        _loadAccountsCts?.Dispose();
    }

    public async Task InitializeAsync()
    {
        await LoadAccountsAsync();

        // Start background sync for all enabled accounts (once at startup)
        foreach (var account in Accounts)
        {
            if (account.IsEnabled)
            {
                _ = _syncManager.StartAccountSyncAsync(account).ContinueWith(t =>
                    _logger.Error(t.Exception, "Sync failed for {Email}", account.EmailAddress),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    private static void PopulateFolderChildren(AccountFolderNode accountNode, IEnumerable<MailFolder> folders)
    {
        accountNode.Children.Clear();
        foreach (var folder in folders.OrderBy(f => f.SpecialUse != SpecialFolderType.Inbox)
                                       .ThenBy(f => f.Name))
        {
            accountNode.Children.Add(new AccountFolderNode
            {
                DisplayName = folder.Name,
                Folder = folder,
                Account = accountNode.Account
            });
        }
    }

    private Account? FindAccountById(int accountId)
        => Accounts.FirstOrDefault(a => a.Id == accountId);

    private AccountFolderNode? FindFolderNode(int folderId)
        => FolderTree.SelectMany(a => a.Children).FirstOrDefault(f => f.Folder?.Id == folderId);

    private async Task UpdateUnreadCountsAsync()
    {
        using var scope = App.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<Core.Data.MailAggregatorDbContext>();

        var folderNodes = FolderTree.SelectMany(a => a.Children)
            .Where(f => f.Folder != null).ToList();
        var folderIds = folderNodes.Select(f => f.Folder!.Id).ToList();

        var counts = await dbContext.Messages
            .Where(m => folderIds.Contains(m.FolderId) && !m.IsRead)
            .GroupBy(m => m.FolderId)
            .Select(g => new { FolderId = g.Key, Count = g.Count() })
            .ToListAsync();

        var countDict = counts.ToDictionary(c => c.FolderId, c => c.Count);

        foreach (var node in folderNodes)
        {
            var newCount = countDict.TryGetValue(node.Folder!.Id, out var count) && count > 0 ? count : (int?)null;
            if (node.UnreadCount != newCount)
                node.UnreadCount = newCount;
        }
    }

    [RelayCommand]
    private async Task LoadAccountsAsync()
    {
        // Cancel any previous in-flight load to abort stale IMAP connections
        // (e.g. connections using old proxy settings from a prior account config)
        _loadAccountsCts?.Cancel();
        _loadAccountsCts?.Dispose();
        _loadAccountsCts = new CancellationTokenSource();
        var ct = _loadAccountsCts.Token;

        try
        {
            StatusText = "Loading accounts...";
            IsSyncing = true;

            var accounts = await _accountService.GetAllAccountsAsync(ct);
            ct.ThrowIfCancellationRequested();
            Accounts = new ObservableCollection<Account>(accounts);

            // Load folders from DB for all accounts (no IMAP connection needed;
            // folders are synced once on first account connection by SyncManager)
            var syncTasks = accounts.Select(async account =>
            {
                try
                {
                    var folders = await _emailSyncService.GetFoldersFromDbAsync(account.Id, ct);
                    return (account, folders: (IReadOnlyList<MailFolder>?)folders);
                }
                catch (OperationCanceledException)
                {
                    return (account, folders: null);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to load folders for {Email}", account.EmailAddress);
                    return (account, folders: null);
                }
            });
            var results = await Task.WhenAll(syncTasks);

            ct.ThrowIfCancellationRequested();

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
                    PopulateFolderChildren(accountNode, folders);
                }

                newTree.Add(accountNode);
            }
            FolderTree = newTree;
            await UpdateUnreadCountsAsync();

            // Default to unified inbox view
            await ShowUnifiedInboxAsync();

            StatusText = $"{accounts.Count} account(s) loaded";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Previous load cancelled by a newer load — expected, no error
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
        catch (IOException ex)
        {
            _logger.Error(ex, "Network error loading folder {FolderName}", node.DisplayName);
            StatusText = $"Network error loading {node.DisplayName} — please click Refresh to retry";
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
            // Per-account error handling: one account failure should not block others
            var syncTasks = inboxFolders
                .GroupBy(f => f.Account!.Id)
                .Select(async group =>
                {
                    var account = group.First().Account!;
                    try
                    {
                        foreach (var f in group)
                        {
                            await _emailSyncService.SyncIncrementalAsync(f.Account!, f.Folder!);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to sync inbox for {Email}, skipping", account.EmailAddress);
                    }
                });
            await Task.WhenAll(syncTasks);

            await LoadEmailsForCurrentViewAsync();

            StatusText = $"Unified Inbox - {Emails.Count} message(s)";
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "Network error loading unified inbox");
            StatusText = "Network error loading inbox — please click Refresh to retry";
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

            var folderNode = FindFolderNode(message.FolderId);
            if (folderNode != null)
                folderNode.UnreadCount = folderNode.UnreadCount > 1 ? folderNode.UnreadCount - 1 : null;
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "Network error marking message as read");
            StatusText = "Network error marking as read — please click Refresh to retry";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to mark message as read");
            StatusText = "Error marking message as read";
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
        catch (IOException ex)
        {
            _logger.Error(ex, "Network error deleting message");
            StatusText = "Network error deleting message — please click Refresh to retry";
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
    private void OpenTwoFactor()
    {
        var vm = App.Services.GetRequiredService<TwoFactorViewModel>();
        var window = new Views.TwoFactorWindow { DataContext = vm };
        window.Show();
    }

    [RelayCommand]
    private void CycleLogLevel()
    {
        var isDebug = App.LogLevelSwitch.MinimumLevel == LogEventLevel.Debug;
        App.LogLevelSwitch.MinimumLevel = isDebug ? LogEventLevel.Information : LogEventLevel.Debug;
        LogLevel = isDebug ? "INFO" : "DEBUG";
        _logger.Information("Log level changed to {Level}", App.LogLevelSwitch.MinimumLevel);
    }

    [RelayCommand]
    private void OpenAccountSettings()
    {
        // Cancel any in-flight load so stale IMAP connections (e.g. using old proxy
        // settings) are aborted immediately rather than running until timeout.
        _loadAccountsCts?.Cancel();
        _loadAccountsCts?.Dispose();
        _loadAccountsCts = null;

        var vm = App.Services.GetRequiredService<AccountListViewModel>();
        var window = new Views.AccountListWindow { DataContext = vm };
        window.ShowDialog();
        // Refresh after account changes and start sync for any new accounts
        _ = ReloadAccountsAndStartSyncAsync().ContinueWith(t =>
            _logger.Error(t.Exception, "Failed to reload accounts"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task ReloadAccountsAndStartSyncAsync()
    {
        await LoadAccountsAsync();

        foreach (var account in Accounts)
        {
            if (account.IsEnabled)
            {
                _ = _syncManager.StartAccountSyncAsync(account).ContinueWith(t =>
                    _logger.Error(t.Exception, "Sync failed for {Email}", account.EmailAddress),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }
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
                // Fetch from IMAP if body not cached yet, or if cached HTML still
                // contains unresolved cid: references (from before inline image resolution was added)
                var hasUnresolvedCid = fullMessage.BodyHtml != null
                    && (fullMessage.BodyHtml.Contains("src=\"cid:", StringComparison.OrdinalIgnoreCase)
                        || fullMessage.BodyHtml.Contains("src='cid:", StringComparison.OrdinalIgnoreCase));
                var needsFetch = (fullMessage.BodyHtml == null && fullMessage.BodyText == null)
                    || hasUnresolvedCid;
                if (needsFetch)
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
        catch (IOException ex)
        {
            _logger.Error(ex, "Network error loading message body");
            StatusText = "Network error loading message — please click Refresh to retry";
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load full message");
            StatusText = "Error loading message body";
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

    private void OnFoldersSynced(object? sender, FoldersSyncedEventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var accountNode = FolderTree.FirstOrDefault(n => n.Account?.Id == e.AccountId);
                if (accountNode == null) return;

                var folders = await _emailSyncService.GetFoldersFromDbAsync(e.AccountId);
                PopulateFolderChildren(accountNode, folders);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to refresh folders for account {AccountId}", e.AccountId);
            }
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
                await UpdateUnreadCountsAsync();
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

    [ObservableProperty]
    private int? _unreadCount;

    public Account? Account { get; set; }
    public MailFolder? Folder { get; set; }
    public bool IsAccount { get; set; }
    public ObservableCollection<AccountFolderNode> Children { get; } = [];
}
