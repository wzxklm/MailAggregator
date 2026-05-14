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
    private readonly IEmailOperationService _emailOperationService;
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
        IEmailOperationService emailOperationService,
        ISyncManager syncManager,
        ILogger logger)
    {
        _accountService = accountService;
        _emailSyncService = emailSyncService;
        _emailOperationService = emailOperationService;
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

    internal static void PopulateFolderChildren(AccountFolderNode accountNode, IEnumerable<MailFolder> folders)
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

    internal Account? FindAccountById(int accountId)
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
}
