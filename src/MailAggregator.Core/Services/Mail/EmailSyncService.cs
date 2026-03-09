using System.Collections.Concurrent;
using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Serilog;

using LocalMailFolder = MailAggregator.Core.Models.MailFolder;

namespace MailAggregator.Core.Services.Mail;

public class EmailSyncService : IEmailSyncService
{
    private const int InitialSyncDays = 30;

    private readonly IImapConnectionPool _connectionPool;
    private readonly IDbContextFactory<MailAggregatorDbContext> _dbContextFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Per-folder locks to prevent concurrent sync operations on the same folder,
    /// which would cause UNIQUE constraint violations on (FolderId, Uid).
    /// </summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _folderSyncLocks = new();

    private SemaphoreSlim GetFolderLock(int folderId)
        => _folderSyncLocks.GetOrAdd(folderId, _ => new SemaphoreSlim(1, 1));

    public EmailSyncService(
        IImapConnectionPool connectionPool,
        IDbContextFactory<MailAggregatorDbContext> dbContextFactory,
        ILogger logger)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await SyncFoldersCoreAsync(account, pooled.Client, dbContext, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersAsync(Account account, ImapClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await SyncFoldersCoreAsync(account, client, dbContext, cancellationToken);
    }

    private async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersCoreAsync(Account account, ImapClient client, MailAggregatorDbContext dbContext, CancellationToken cancellationToken)
    {
        var personalNamespace = client.PersonalNamespaces[0];
        var imapFolders = await client.GetFoldersAsync(personalNamespace, cancellationToken: cancellationToken);

        var localFolders = await dbContext.Folders
            .Where(f => f.AccountId == account.Id)
            .ToListAsync(cancellationToken);

        var localFolderMap = localFolders.ToDictionary(f => f.FullName);
        var serverFolderNames = new HashSet<string>();

        foreach (var imapFolder in imapFolders)
        {
            // Skip non-selectable container folders (e.g. QQ Mail's "其他文件夹")
            if (imapFolder.Attributes.HasFlag(FolderAttributes.NoSelect))
                continue;

            serverFolderNames.Add(imapFolder.FullName);

            if (localFolderMap.TryGetValue(imapFolder.FullName, out var existing))
            {
                existing.Name = imapFolder.Name;
                existing.SpecialUse = MapSpecialUse(imapFolder.Attributes);
            }
            else
            {
                var newFolder = new LocalMailFolder
                {
                    AccountId = account.Id,
                    Name = imapFolder.Name,
                    FullName = imapFolder.FullName,
                    SpecialUse = MapSpecialUse(imapFolder.Attributes)
                };
                dbContext.Folders.Add(newFolder);
            }
        }

        // Remove local folders no longer on server
        var removedFolders = localFolders.Where(f => !serverFolderNames.Contains(f.FullName)).ToList();
        dbContext.Folders.RemoveRange(removedFolders);

        await SaveChangesSafeAsync(dbContext, cancellationToken);

        _logger.Information("Synced {Count} folders for {Email}", serverFolderNames.Count, account.EmailAddress);

        // Return tracked entities directly instead of re-querying
        return localFolders.Where(f => serverFolderNames.Contains(f.FullName))
            .Concat(dbContext.Folders.Local.Where(f => f.AccountId == account.Id && !localFolderMap.ContainsKey(f.FullName)))
            .ToList();
    }

    public async Task SyncInitialAsync(Account account, LocalMailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);

        var folderLock = GetFolderLock(folder.Id);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            await SyncInitialCoreAsync(account, folder, cancellationToken);
        }
        finally
        {
            folderLock.Release();
        }
    }

    private async Task SyncInitialCoreAsync(Account account, LocalMailFolder folder, CancellationToken cancellationToken)
    {
        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        await SyncInitialCoreAsync(account, folder, pooled.Client, cancellationToken);
    }

    private async Task SyncInitialCoreAsync(Account account, LocalMailFolder folder, ImapClient client, CancellationToken cancellationToken)
    {
        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        // Update UIDVALIDITY
        folder.UidValidity = imapFolder.UidValidity;

        // Search for messages from the last 30 days
        var sinceDate = DateTimeOffset.UtcNow.AddDays(-InitialSyncDays);
        var query = SearchQuery.DeliveredAfter(sinceDate.DateTime);
        var uids = await imapFolder.SearchAsync(query, cancellationToken);

        _logger.Information("Initial sync: found {Count} messages in {Folder} for {Email}",
            uids.Count, folder.FullName, account.EmailAddress);

        if (uids.Count > 0)
        {
            await FetchAndCacheMessagesAsync(imapFolder, folder, account, uids, dbContext, cancellationToken);
            folder.MaxUid = uids.Max(u => u.Id);
        }

        folder.MessageCount = imapFolder.Count;
        folder.UnreadCount = imapFolder.Unread;

        dbContext.Folders.Update(folder);
        await SaveChangesSafeAsync(dbContext, cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);
    }

    public async Task SyncIncrementalAsync(Account account, LocalMailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);

        var folderLock = GetFolderLock(folder.Id);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
            using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await SyncIncrementalCoreAsync(account, folder, pooled.Client, dbContext, cancellationToken);
        }
        finally
        {
            folderLock.Release();
        }
    }

    public async Task SyncIncrementalAsync(Account account, LocalMailFolder folder, ImapClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);

        var folderLock = GetFolderLock(folder.Id);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await SyncIncrementalCoreAsync(account, folder, client, dbContext, cancellationToken);
        }
        finally
        {
            folderLock.Release();
        }
    }

    private async Task SyncIncrementalCoreAsync(Account account, LocalMailFolder folder, ImapClient client, MailAggregatorDbContext dbContext, CancellationToken cancellationToken)
    {
        var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
        try
        {
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        }
        catch (ImapCommandException ex) when (ex.Response == ImapCommandResponse.No)
        {
            _logger.Warning("Folder {Folder} is not selectable for {Email}, removing from local cache",
                folder.FullName, account.EmailAddress);
            dbContext.Folders.Remove(folder);
            await SaveChangesSafeAsync(dbContext, cancellationToken);
            return;
        }

        // Check UIDVALIDITY - if changed, reset folder cache
        if (imapFolder.UidValidity != folder.UidValidity)
        {
            _logger.Warning("UIDVALIDITY changed for {Folder} in {Email} (was {Old}, now {New}). Resetting cache.",
                folder.FullName, account.EmailAddress, folder.UidValidity, imapFolder.UidValidity);

            await dbContext.Messages
                .Where(m => m.FolderId == folder.Id)
                .ExecuteDeleteAsync(cancellationToken);

            folder.UidValidity = imapFolder.UidValidity;
            folder.MaxUid = 0;

            // Perform initial sync instead, reusing the caller's connection to avoid
            // saturating the pool. Call core method directly since caller already holds the folder lock.
            await imapFolder.CloseAsync(false, cancellationToken);
            await SyncInitialCoreAsync(account, folder, client, cancellationToken);
            return;
        }

        // Fetch new messages (UID > MaxUid)
        if (folder.MaxUid > 0)
        {
            var range = new UniqueIdRange(new UniqueId(folder.MaxUid + 1), UniqueId.MaxValue);
            var uids = await imapFolder.SearchAsync(SearchQuery.Uids(range), cancellationToken);

            if (uids.Count > 0)
            {
                _logger.Information("Incremental sync: {Count} new messages in {Folder} for {Email}",
                    uids.Count, folder.FullName, account.EmailAddress);

                await FetchAndCacheMessagesAsync(imapFolder, folder, account, uids, dbContext, cancellationToken);
                folder.MaxUid = uids.Max(u => u.Id);
            }
        }

        // Sync flags and detect deletions in a single IMAP roundtrip
        await SyncFlagsAndDetectDeletionsAsync(imapFolder, folder, dbContext, cancellationToken);

        folder.MessageCount = imapFolder.Count;
        folder.UnreadCount = imapFolder.Unread;

        // Use Attach + mark-modified instead of Update to avoid graph traversal
        // cascading to EmailMessage entities via the Messages navigation property.
        dbContext.Folders.Attach(folder);
        dbContext.Entry(folder).Property(f => f.MaxUid).IsModified = true;
        dbContext.Entry(folder).Property(f => f.MessageCount).IsModified = true;
        dbContext.Entry(folder).Property(f => f.UnreadCount).IsModified = true;
        await SaveChangesSafeAsync(dbContext, cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);
    }

    public async Task SetMessageReadAsync(Account account, EmailMessage message, bool isRead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var folder = await dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} not found.");

        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        var client = pooled.Client;
        var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
        await imapFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var uid = new UniqueId(message.Uid);
        if (isRead)
            await imapFolder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
        else
            await imapFolder.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);

        message.IsRead = isRead;
        var tracked = dbContext.ChangeTracker.Entries<EmailMessage>()
            .FirstOrDefault(e => e.Entity.Id == message.Id);

        if (tracked != null)
        {
            tracked.Entity.IsRead = isRead;
            tracked.Property(m => m.IsRead).IsModified = true;
        }
        else
        {
            dbContext.Messages.Attach(message);
            dbContext.Entry(message).Property(m => m.IsRead).IsModified = true;
        }
        await dbContext.SaveChangesAsync(cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);

        _logger.Information("Set message UID {Uid} in {Folder} as {ReadStatus} for {Email}",
            message.Uid, folder.FullName, isRead ? "read" : "unread", account.EmailAddress);
    }

    public async Task DownloadAttachmentAsync(Account account, EmailMessage message, EmailAttachment attachment, string savePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentException.ThrowIfNullOrEmpty(savePath);

        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var folder = await dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} not found.");

        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        var imapFolder = await pooled.Client.GetFolderAsync(folder.FullName, cancellationToken);
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var uid = new UniqueId(message.Uid);
        var mimeMessage = await imapFolder.GetMessageAsync(uid, cancellationToken);

        var mimePart = FindAttachmentPart(mimeMessage, attachment);
        if (mimePart == null)
            throw new InvalidOperationException($"Attachment '{attachment.FileName}' not found in message.");

        var directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (mimePart.Content == null)
            throw new InvalidOperationException($"Attachment '{attachment.FileName}' has no content.");

        using var stream = File.Create(savePath);
        await mimePart.Content.DecodeToAsync(stream, cancellationToken);

        attachment.LocalPath = savePath;
        dbContext.Attachments.Update(attachment);
        await dbContext.SaveChangesAsync(cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);

        _logger.Information("Downloaded attachment '{FileName}' for message UID {Uid} in {Email}",
            attachment.FileName, message.Uid, account.EmailAddress);
    }

    public async Task MoveMessageAsync(Account account, EmailMessage message, LocalMailFolder destinationFolder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destinationFolder);

        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var sourceFolder = await dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
            ?? throw new InvalidOperationException($"Source folder {message.FolderId} not found.");

        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        var client = pooled.Client;
        var imapSourceFolder = await client.GetFolderAsync(sourceFolder.FullName, cancellationToken);
        var imapDestFolder = await client.GetFolderAsync(destinationFolder.FullName, cancellationToken);

        await imapSourceFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var uid = new UniqueId(message.Uid);
        var newUid = await imapSourceFolder.MoveToAsync(uid, imapDestFolder, cancellationToken);

        // Update local cache
        message.FolderId = destinationFolder.Id;
        if (newUid.HasValue)
            message.Uid = newUid.Value.Id;

        dbContext.Messages.Update(message);
        await dbContext.SaveChangesAsync(cancellationToken);

        await imapSourceFolder.CloseAsync(false, cancellationToken);

        _logger.Information("Moved message UID {Uid} from {Source} to {Dest} for {Email}",
            message.Uid, sourceFolder.FullName, destinationFolder.FullName, account.EmailAddress);
    }

    public async Task DeleteMessageAsync(Account account, EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var trashFolder = await dbContext.Folders
            .FirstOrDefaultAsync(f => f.AccountId == account.Id && f.SpecialUse == SpecialFolderType.Trash, cancellationToken)
            ?? throw new InvalidOperationException("Trash folder not found for this account. Sync folders first.");

        // If already in Trash, just mark as deleted and expunge
        if (message.FolderId == trashFolder.Id)
        {
            using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
            var imapFolder = await pooled.Client.GetFolderAsync(trashFolder.FullName, cancellationToken);
            await imapFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

            var uid = new UniqueId(message.Uid);
            await imapFolder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken);
            await imapFolder.ExpungeAsync(cancellationToken);

            dbContext.Messages.Remove(message);
            await dbContext.SaveChangesAsync(cancellationToken);

            await imapFolder.CloseAsync(false, cancellationToken);

            _logger.Information("Permanently deleted message UID {Uid} from Trash for {Email}",
                message.Uid, account.EmailAddress);
            return;
        }

        await MoveMessageAsync(account, message, trashFolder, cancellationToken);
        _logger.Information("Moved message UID {Uid} to Trash for {Email}", message.Uid, account.EmailAddress);
    }

    public async Task FetchMessageBodyAsync(Account account, EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var folder = await dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} not found.");

        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        var imapFolder = await pooled.Client.GetFolderAsync(folder.FullName, cancellationToken);
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var uid = new UniqueId(message.Uid);
        var mimeMessage = await imapFolder.GetMessageAsync(uid, cancellationToken);

        message.BodyText = mimeMessage.TextBody;
        message.BodyHtml = mimeMessage.HtmlBody;
        if (mimeMessage.References != null && mimeMessage.References.Count > 0)
            message.References = string.Join(" ", mimeMessage.References);

        // Cache attachment metadata if not already present
        if (message.Attachments.Count == 0)
        {
            foreach (var attachment in mimeMessage.Attachments.OfType<MimePart>())
            {
                message.Attachments.Add(new EmailAttachment
                {
                    FileName = attachment.FileName ?? "unnamed",
                    ContentType = attachment.ContentType?.MimeType,
                    Size = attachment.Content?.Stream?.Length ?? 0,
                    ContentId = attachment.ContentId
                });
            }
        }

        message.HasAttachments = message.Attachments.Count > 0;

        var tracked = dbContext.ChangeTracker.Entries<EmailMessage>()
            .FirstOrDefault(e => e.Entity.Id == message.Id);
        if (tracked != null)
        {
            tracked.Entity.BodyText = message.BodyText;
            tracked.Entity.BodyHtml = message.BodyHtml;
            tracked.Entity.References = message.References;
            tracked.Entity.HasAttachments = message.HasAttachments;
        }
        else
        {
            dbContext.Messages.Attach(message);
            dbContext.Entry(message).Property(m => m.BodyText).IsModified = true;
            dbContext.Entry(message).Property(m => m.BodyHtml).IsModified = true;
            dbContext.Entry(message).Property(m => m.References).IsModified = true;
            dbContext.Entry(message).Property(m => m.HasAttachments).IsModified = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await imapFolder.CloseAsync(false, cancellationToken);

        _logger.Debug("Fetched body for UID {Uid} in {Folder} for {Email}",
            message.Uid, folder.FullName, account.EmailAddress);
    }

    private async Task FetchAndCacheMessagesAsync(
        IMailFolder imapFolder,
        LocalMailFolder localFolder,
        Account account,
        IList<UniqueId> uids,
        MailAggregatorDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var existingUidList = await dbContext.Messages
            .Where(m => m.FolderId == localFolder.Id)
            .Select(m => m.Uid)
            .ToListAsync(cancellationToken);
        var existingUids = existingUidList.ToHashSet();

        var newUids = uids.Where(u => !existingUids.Contains(u.Id)).ToList();
        if (newUids.Count == 0) return;

        // Fetch summaries first
        var summaryItems = MessageSummaryItems.UniqueId
            | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags
            | MessageSummaryItems.BodyStructure
            | MessageSummaryItems.PreviewText;

        var summaries = await imapFolder.FetchAsync(newUids, summaryItems, cancellationToken);
        const int batchSize = 50;
        int processedInBatch = 0;

        foreach (var summary in summaries)
        {
            var envelope = summary.Envelope;
            if (envelope == null) continue;

            var emailMessage = new EmailMessage
            {
                AccountId = account.Id,
                FolderId = localFolder.Id,
                Uid = summary.UniqueId.Id,
                MessageId = envelope.MessageId,
                InReplyTo = envelope.InReplyTo,
                FromAddress = envelope.From?.Mailboxes?.FirstOrDefault()?.Address ?? string.Empty,
                FromName = envelope.From?.Mailboxes?.FirstOrDefault()?.Name,
                ToAddresses = FormatAddressList(envelope.To),
                CcAddresses = FormatAddressList(envelope.Cc),
                BccAddresses = FormatAddressList(envelope.Bcc),
                Subject = envelope.Subject,
                DateSent = envelope.Date ?? DateTimeOffset.MinValue,
                PreviewText = summary.PreviewText,
                IsRead = summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                IsFlagged = summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
                HasAttachments = summary.Attachments?.Any() ?? false
            };

            // Body and attachments are fetched lazily when the user opens the email
            // (via FetchMessageBodyAsync). This avoids N IMAP round-trips during sync.

            dbContext.Messages.Add(emailMessage);
            processedInBatch++;

            if (processedInBatch >= batchSize)
            {
                await SaveChangesSafeAsync(dbContext, cancellationToken);
                processedInBatch = 0;
            }
        }

        if (processedInBatch > 0)
            await SaveChangesSafeAsync(dbContext, cancellationToken);
    }

    /// <summary>
    /// Syncs server-side flag changes and detects deleted messages in a single IMAP FETCH.
    /// UIDs returned by FETCH still exist; local UIDs absent from the response have been deleted.
    /// </summary>
    private async Task SyncFlagsAndDetectDeletionsAsync(IMailFolder imapFolder, LocalMailFolder localFolder, MailAggregatorDbContext dbContext, CancellationToken cancellationToken)
    {
        var localMessages = await dbContext.Messages
            .Where(m => m.FolderId == localFolder.Id)
            .Select(m => new { m.Id, m.Uid, m.IsRead, m.IsFlagged })
            .ToListAsync(cancellationToken);

        if (localMessages.Count == 0) return;

        var localUids = localMessages.Select(m => new UniqueId(m.Uid)).ToList();

        // Single IMAP FETCH: server returns only UIDs that still exist
        var summaries = await imapFolder.FetchAsync(localUids, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, cancellationToken);

        var serverFlags = summaries.ToDictionary(
            s => s.UniqueId.Id,
            s => (IsRead: s.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                  IsFlagged: s.Flags?.HasFlag(MessageFlags.Flagged) ?? false));

        // Flag sync: update changed flags
        var updatedCount = 0;
        var deletedUids = new List<uint>();

        // Build lookup once to avoid O(n*m) ChangeTracker scan inside the loop.
        // Entities may already be tracked from FetchAndCacheMessagesAsync in the same DbContext.
        var trackedEntries = dbContext.ChangeTracker.Entries<EmailMessage>()
            .ToDictionary(e => e.Entity.Id);

        foreach (var local in localMessages)
        {
            if (!serverFlags.TryGetValue(local.Uid, out var server))
            {
                // UID not in server response = message deleted
                deletedUids.Add(local.Uid);
                continue;
            }

            if (local.IsRead == server.IsRead && local.IsFlagged == server.IsFlagged)
                continue;

            if (trackedEntries.TryGetValue(local.Id, out var tracked))
            {
                if (local.IsRead != server.IsRead)
                {
                    tracked.Entity.IsRead = server.IsRead;
                    tracked.Property(m => m.IsRead).IsModified = true;
                }
                if (local.IsFlagged != server.IsFlagged)
                {
                    tracked.Entity.IsFlagged = server.IsFlagged;
                    tracked.Property(m => m.IsFlagged).IsModified = true;
                }
            }
            else
            {
                var entity = new EmailMessage { Id = local.Id, IsRead = server.IsRead, IsFlagged = server.IsFlagged };
                dbContext.Messages.Attach(entity);
                if (local.IsRead != server.IsRead)
                    dbContext.Entry(entity).Property(m => m.IsRead).IsModified = true;
                if (local.IsFlagged != server.IsFlagged)
                    dbContext.Entry(entity).Property(m => m.IsFlagged).IsModified = true;
            }

            updatedCount++;
        }

        if (updatedCount > 0)
        {
            await SaveChangesSafeAsync(dbContext, cancellationToken);
            _logger.Information("Synced flags for {Count} message(s) in {Folder}", updatedCount, localFolder.FullName);
        }

        // Deletion detection: remove local messages no longer on server
        if (deletedUids.Count > 0)
        {
            var count = await dbContext.Messages
                .Where(m => m.FolderId == localFolder.Id && deletedUids.Contains(m.Uid))
                .ExecuteDeleteAsync(cancellationToken);

            _logger.Information("Detected and removed {Count} deleted messages in {Folder}", count, localFolder.FullName);
        }
    }

    internal static SpecialFolderType MapSpecialUse(FolderAttributes attributes)
    {
        if (attributes.HasFlag(FolderAttributes.Inbox)) return SpecialFolderType.Inbox;
        if (attributes.HasFlag(FolderAttributes.Sent)) return SpecialFolderType.Sent;
        if (attributes.HasFlag(FolderAttributes.Drafts)) return SpecialFolderType.Drafts;
        if (attributes.HasFlag(FolderAttributes.Trash)) return SpecialFolderType.Trash;
        if (attributes.HasFlag(FolderAttributes.Junk)) return SpecialFolderType.Junk;
        if (attributes.HasFlag(FolderAttributes.Archive)) return SpecialFolderType.Archive;
        return SpecialFolderType.None;
    }

    private static string FormatAddressList(InternetAddressList? addresses)
    {
        if (addresses == null || addresses.Count == 0) return string.Empty;
        return string.Join(", ", addresses.Mailboxes.Select(m =>
            string.IsNullOrEmpty(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));
    }

    /// <summary>
    /// Saves changes, gracefully handling concurrency conflicts and UNIQUE constraint
    /// violations from parallel sync operations.
    /// </summary>
    private async Task SaveChangesSafeAsync(MailAggregatorDbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.Warning("Concurrency conflict on {Count} entity(ies), detaching stale entries and retrying",
                ex.Entries.Count);
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }
            if (dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // UNIQUE constraint violation (e.g. duplicate FolderId+Uid from concurrent sync).
            // Detach the duplicate Added entries — the other sync already inserted them.
            _logger.Warning("UNIQUE constraint conflict during save, detaching duplicate entries and retrying");
            foreach (var entry in dbContext.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added)
                .ToList())
            {
                entry.State = EntityState.Detached;
            }
            if (dbContext.ChangeTracker.HasChanges())
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private static MimePart? FindAttachmentPart(MimeMessage message, EmailAttachment attachment)
    {
        foreach (var part in message.Attachments.OfType<MimePart>())
        {
            if ((part.FileName == attachment.FileName || part.ContentId == attachment.ContentId)
                && (attachment.ContentType == null || part.ContentType?.MimeType == attachment.ContentType))
            {
                return part;
            }
        }
        return null;
    }
}
