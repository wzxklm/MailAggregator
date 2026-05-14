using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MailAggregator.Desktop.ViewModels;

public partial class MainViewModel
{
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

            await _emailOperationService.SetMessageReadAsync(account, message, true);
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

            await _emailOperationService.DeleteMessageAsync(account, message);
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
                        await _emailOperationService.FetchMessageBodyAsync(account, fullMessage);
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
