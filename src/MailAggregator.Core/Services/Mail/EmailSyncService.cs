using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Serilog;

using LocalMailFolder = MailAggregator.Core.Models.MailFolder;

namespace MailAggregator.Core.Services.Mail;

public class EmailSyncService : IEmailSyncService
{
    private const int InitialSyncDays = 30;

    private readonly IImapConnectionPool _connectionPool;
    private readonly MailAggregatorDbContext _dbContext;
    private readonly ILogger _logger;

    public EmailSyncService(
        IImapConnectionPool connectionPool,
        MailAggregatorDbContext dbContext,
        ILogger logger)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        return await SyncFoldersCoreAsync(account, pooled.Client, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersAsync(Account account, ImapClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        return await SyncFoldersCoreAsync(account, client, cancellationToken);
    }

    private async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersCoreAsync(Account account, ImapClient client, CancellationToken cancellationToken)
    {
        var personalNamespace = client.PersonalNamespaces[0];
        var imapFolders = await client.GetFoldersAsync(personalNamespace, cancellationToken: cancellationToken);

        var localFolders = await _dbContext.Folders
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
                _dbContext.Folders.Add(newFolder);
            }
        }

        // Remove local folders no longer on server
        var removedFolders = localFolders.Where(f => !serverFolderNames.Contains(f.FullName)).ToList();
        _dbContext.Folders.RemoveRange(removedFolders);

        await SaveChangesSafeAsync(cancellationToken);

        _logger.Information("Synced {Count} folders for {Email}", serverFolderNames.Count, account.EmailAddress);

        // Return tracked entities directly instead of re-querying
        return localFolders.Where(f => serverFolderNames.Contains(f.FullName))
            .Concat(_dbContext.Folders.Local.Where(f => f.AccountId == account.Id && !localFolderMap.ContainsKey(f.FullName)))
            .ToList();
    }

    public async Task SyncInitialAsync(Account account, LocalMailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);

        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        var client = pooled.Client;
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
            await FetchAndCacheMessagesAsync(imapFolder, folder, account, uids, cancellationToken);
            folder.MaxUid = uids.Max(u => u.Id);
        }

        folder.MessageCount = imapFolder.Count;
        folder.UnreadCount = imapFolder.Unread;

        _dbContext.Folders.Update(folder);
        await SaveChangesSafeAsync(cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);
    }

    public async Task SyncIncrementalAsync(Account account, LocalMailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);
        using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
        await SyncIncrementalCoreAsync(account, folder, pooled.Client, cancellationToken);
    }

    public async Task SyncIncrementalAsync(Account account, LocalMailFolder folder, ImapClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);
        await SyncIncrementalCoreAsync(account, folder, client, cancellationToken);
    }

    private async Task SyncIncrementalCoreAsync(Account account, LocalMailFolder folder, ImapClient client, CancellationToken cancellationToken)
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
            _dbContext.Folders.Remove(folder);
            await SaveChangesSafeAsync(cancellationToken);
            return;
        }

        // Check UIDVALIDITY - if changed, reset folder cache
        if (imapFolder.UidValidity != folder.UidValidity)
        {
            _logger.Warning("UIDVALIDITY changed for {Folder} in {Email} (was {Old}, now {New}). Resetting cache.",
                folder.FullName, account.EmailAddress, folder.UidValidity, imapFolder.UidValidity);

            await _dbContext.Messages
                .Where(m => m.FolderId == folder.Id)
                .ExecuteDeleteAsync(cancellationToken);

            folder.UidValidity = imapFolder.UidValidity;
            folder.MaxUid = 0;

            // Perform initial sync instead (creates its own connection)
            await imapFolder.CloseAsync(false, cancellationToken);
            await SyncInitialAsync(account, folder, cancellationToken);
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

                await FetchAndCacheMessagesAsync(imapFolder, folder, account, uids, cancellationToken);
                folder.MaxUid = uids.Max(u => u.Id);
            }
        }

        // Detect deleted messages
        await DetectDeletedMessagesAsync(imapFolder, folder, cancellationToken);

        folder.MessageCount = imapFolder.Count;
        folder.UnreadCount = imapFolder.Unread;

        _dbContext.Folders.Update(folder);
        await SaveChangesSafeAsync(cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);
    }

    public async Task SetMessageReadAsync(Account account, EmailMessage message, bool isRead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        var folder = await _dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
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
        var tracked = _dbContext.ChangeTracker.Entries<EmailMessage>()
            .FirstOrDefault(e => e.Entity.Id == message.Id);

        if (tracked != null)
        {
            tracked.Entity.IsRead = isRead;
            tracked.Property(m => m.IsRead).IsModified = true;
        }
        else
        {
            _dbContext.Messages.Attach(message);
            _dbContext.Entry(message).Property(m => m.IsRead).IsModified = true;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

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

        var folder = await _dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
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
        _dbContext.Attachments.Update(attachment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);

        _logger.Information("Downloaded attachment '{FileName}' for message UID {Uid} in {Email}",
            attachment.FileName, message.Uid, account.EmailAddress);
    }

    public async Task MoveMessageAsync(Account account, EmailMessage message, LocalMailFolder destinationFolder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(destinationFolder);

        var sourceFolder = await _dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
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

        _dbContext.Messages.Update(message);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await imapSourceFolder.CloseAsync(false, cancellationToken);

        _logger.Information("Moved message UID {Uid} from {Source} to {Dest} for {Email}",
            message.Uid, sourceFolder.FullName, destinationFolder.FullName, account.EmailAddress);
    }

    public async Task DeleteMessageAsync(Account account, EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        var trashFolder = await _dbContext.Folders
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

            _dbContext.Messages.Remove(message);
            await _dbContext.SaveChangesAsync(cancellationToken);

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

        var folder = await _dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
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

        var tracked = _dbContext.ChangeTracker.Entries<EmailMessage>()
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
            _dbContext.Messages.Attach(message);
            _dbContext.Entry(message).Property(m => m.BodyText).IsModified = true;
            _dbContext.Entry(message).Property(m => m.BodyHtml).IsModified = true;
            _dbContext.Entry(message).Property(m => m.References).IsModified = true;
            _dbContext.Entry(message).Property(m => m.HasAttachments).IsModified = true;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await imapFolder.CloseAsync(false, cancellationToken);

        _logger.Debug("Fetched body for UID {Uid} in {Folder} for {Email}",
            message.Uid, folder.FullName, account.EmailAddress);
    }

    private async Task FetchAndCacheMessagesAsync(
        IMailFolder imapFolder,
        LocalMailFolder localFolder,
        Account account,
        IList<UniqueId> uids,
        CancellationToken cancellationToken)
    {
        var existingUidList = await _dbContext.Messages
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
                HasAttachments = summary.Attachments?.Any() ?? false
            };

            // Body and attachments are fetched lazily when the user opens the email
            // (via FetchMessageBodyAsync). This avoids N IMAP round-trips during sync.

            _dbContext.Messages.Add(emailMessage);
            processedInBatch++;

            if (processedInBatch >= batchSize)
            {
                await SaveChangesSafeAsync(cancellationToken);
                processedInBatch = 0;
            }
        }

        if (processedInBatch > 0)
            await SaveChangesSafeAsync(cancellationToken);
    }

    private async Task DetectDeletedMessagesAsync(IMailFolder imapFolder, LocalMailFolder localFolder, CancellationToken cancellationToken)
    {
        var allServerUids = await imapFolder.SearchAsync(SearchQuery.All, cancellationToken);
        var serverUidSet = allServerUids.Select(u => u.Id).ToHashSet();

        var localUids = await _dbContext.Messages
            .Where(m => m.FolderId == localFolder.Id)
            .Select(m => m.Uid)
            .ToListAsync(cancellationToken);

        var deletedUids = localUids.Where(uid => !serverUidSet.Contains(uid)).ToList();

        if (deletedUids.Count > 0)
        {
            // Use ExecuteDeleteAsync to delete in a single SQL statement without loading entities
            var count = await _dbContext.Messages
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
    /// Saves changes, gracefully handling concurrency conflicts from parallel sync operations.
    /// Conflicting (stale) entities are detached and remaining changes are retried.
    /// </summary>
    private async Task SaveChangesSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.Warning("Concurrency conflict on {Count} entity(ies), detaching stale entries and retrying",
                ex.Entries.Count);
            foreach (var entry in ex.Entries)
            {
                entry.State = EntityState.Detached;
            }
            if (_dbContext.ChangeTracker.HasChanges())
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
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
