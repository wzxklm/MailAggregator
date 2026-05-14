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
    private readonly ImapFolderDiscovery _folderDiscovery;
    private readonly ILogger _logger;

    /// <summary>
    /// Per-folder locks to prevent concurrent sync operations on the same folder,
    /// which would cause UNIQUE constraint violations on (FolderId, Uid).
    /// </summary>
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _folderSyncLocks = new();

    private SemaphoreSlim GetFolderLock(int folderId)
        => _folderSyncLocks.GetOrAdd(folderId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Gets a pooled connection and executes the action, returning its result. Retries once on IOException
    /// (dead pooled connection whose TCP socket was silently dropped by firewall/NAT).
    /// </summary>
    private async Task<T> WithPooledConnectionAsync<T>(Account account, Func<ImapClient, Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
            try
            {
                return await action(pooled.Client);
            }
            catch (IOException) when (attempt == 0)
            {
                // Dead pooled connection — let it be disposed, then retry with a fresh one
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    public EmailSyncService(
        IImapConnectionPool connectionPool,
        IDbContextFactory<MailAggregatorDbContext> dbContextFactory,
        ILogger logger)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _folderDiscovery = new ImapFolderDiscovery(logger);
    }

    public async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        return await WithPooledConnectionAsync(account, async client =>
        {
            using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            return await SyncFoldersCoreAsync(account, client, dbContext, cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersAsync(Account account, ImapClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await SyncFoldersCoreAsync(account, client, dbContext, cancellationToken);
    }

    private async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersCoreAsync(Account account, ImapClient client, MailAggregatorDbContext dbContext, CancellationToken cancellationToken)
    {
        var imapFolders = await _folderDiscovery.DiscoverFoldersAsync(account, client, cancellationToken);

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

    public async Task<IReadOnlyList<LocalMailFolder>> GetFoldersFromDbAsync(int accountId, CancellationToken cancellationToken = default)
    {
        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.Folders
            .Where(f => f.AccountId == accountId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
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
            _ = await FetchAndCacheMessagesAsync(imapFolder, folder, account, uids, dbContext, cancellationToken);
            folder.MaxUid = uids.Max(u => u.Id);
        }

        folder.MessageCount = imapFolder.Count;
        folder.UnreadCount = imapFolder.Unread;

        dbContext.Folders.Update(folder);
        await SaveChangesSafeAsync(dbContext, cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);
    }

    public async Task<int> SyncIncrementalAsync(Account account, LocalMailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);

        var folderLock = GetFolderLock(folder.Id);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            return await WithPooledConnectionAsync(account, async client =>
            {
                using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                return await SyncIncrementalCoreAsync(account, folder, client, dbContext, cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            folderLock.Release();
        }
    }

    public async Task<int> SyncIncrementalAsync(Account account, LocalMailFolder folder, ImapClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);

        var folderLock = GetFolderLock(folder.Id);
        await folderLock.WaitAsync(cancellationToken);
        try
        {
            using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            return await SyncIncrementalCoreAsync(account, folder, client, dbContext, cancellationToken);
        }
        finally
        {
            folderLock.Release();
        }
    }

    private async Task<int> SyncIncrementalCoreAsync(Account account, LocalMailFolder folder, ImapClient client, MailAggregatorDbContext dbContext, CancellationToken cancellationToken)
    {
        var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
        try
        {
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        }
        catch (ImapCommandException ex) when (ex.Response == ImapCommandResponse.No)
        {
            // Distinguish auth/access errors from genuinely non-selectable folders.
            // Re-throw so SyncManager can stop the sync loop instead of deleting the folder.
            if (MailConnectionHelper.IsNonTransientAuthError(ex))
            {
                _logger.Warning("Folder {Folder} access denied for {Email}: {Reason}",
                    folder.FullName, account.EmailAddress, ex.ResponseText ?? ex.Message);
                throw;
            }

            _logger.Warning("Folder {Folder} is not selectable for {Email}, removing from local cache",
                folder.FullName, account.EmailAddress);
            dbContext.Folders.Remove(folder);
            await SaveChangesSafeAsync(dbContext, cancellationToken);
            return 0;
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
            return 0;
        }

        // Refresh MaxUid from DB in case another sync caller updated it
        var dbMaxUid = await dbContext.Folders
            .Where(f => f.Id == folder.Id)
            .Select(f => f.MaxUid)
            .FirstOrDefaultAsync(cancellationToken);
        if (dbMaxUid > folder.MaxUid)
            folder.MaxUid = dbMaxUid;

        // Fetch new messages (UID > MaxUid)
        var newCount = 0;
        if (folder.MaxUid > 0)
        {
            var range = new UniqueIdRange(new UniqueId(folder.MaxUid + 1), UniqueId.MaxValue);
            var uids = await imapFolder.SearchAsync(SearchQuery.Uids(range), cancellationToken);

            if (uids.Count > 0)
            {
                newCount = await FetchAndCacheMessagesAsync(imapFolder, folder, account, uids, dbContext, cancellationToken);
                folder.MaxUid = uids.Max(u => u.Id);

                if (newCount > 0)
                {
                    _logger.Information("Incremental sync: {Count} new messages in {Folder} for {Email}",
                        newCount, folder.FullName, account.EmailAddress);
                }
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
        return newCount;
    }

    private async Task<int> FetchAndCacheMessagesAsync(
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
        if (newUids.Count == 0) return 0;

        // Fetch summaries first
        var summaryItems = MessageSummaryItems.UniqueId
            | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags
            | MessageSummaryItems.BodyStructure
            | MessageSummaryItems.PreviewText;

        var summaries = await imapFolder.FetchAsync(newUids, summaryItems, cancellationToken);
        const int batchSize = 50;
        int processedInBatch = 0;
        int totalInserted = 0;

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
            totalInserted++;

            if (processedInBatch >= batchSize)
            {
                await SaveChangesSafeAsync(dbContext, cancellationToken);
                processedInBatch = 0;
            }
        }

        if (processedInBatch > 0)
            await SaveChangesSafeAsync(dbContext, cancellationToken);

        return totalInserted;
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
}
