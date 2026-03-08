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

    private readonly IImapConnectionService _imapConnection;
    private readonly MailAggregatorDbContext _dbContext;
    private readonly ILogger _logger;

    public EmailSyncService(
        IImapConnectionService imapConnection,
        MailAggregatorDbContext dbContext,
        ILogger logger)
    {
        _imapConnection = imapConnection ?? throw new ArgumentNullException(nameof(imapConnection));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<LocalMailFolder>> SyncFoldersAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        using var client = await _imapConnection.ConnectAsync(account, cancellationToken);

        var personalNamespace = client.PersonalNamespaces[0];
        var imapFolders = await client.GetFoldersAsync(personalNamespace, cancellationToken: cancellationToken);

        var localFolders = await _dbContext.Folders
            .Where(f => f.AccountId == account.Id)
            .ToListAsync(cancellationToken);

        var localFolderMap = localFolders.ToDictionary(f => f.FullName);
        var serverFolderNames = new HashSet<string>();

        foreach (var imapFolder in imapFolders)
        {
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

        await _dbContext.SaveChangesAsync(cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

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

        using var client = await _imapConnection.ConnectAsync(account, cancellationToken);
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
        await _dbContext.SaveChangesAsync(cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async Task SyncIncrementalAsync(Account account, LocalMailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(folder);

        using var client = await _imapConnection.ConnectAsync(account, cancellationToken);
        var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
        await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

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

            // Perform initial sync instead
            await imapFolder.CloseAsync(false, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
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
        await _dbContext.SaveChangesAsync(cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }

    public async Task SetMessageReadAsync(Account account, EmailMessage message, bool isRead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        var folder = await _dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} not found.");

        using var client = await _imapConnection.ConnectAsync(account, cancellationToken);
        var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
        await imapFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        var uid = new UniqueId(message.Uid);
        if (isRead)
            await imapFolder.AddFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);
        else
            await imapFolder.RemoveFlagsAsync(uid, MessageFlags.Seen, true, cancellationToken);

        message.IsRead = isRead;
        _dbContext.Messages.Update(message);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await imapFolder.CloseAsync(false, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

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

        using var client = await _imapConnection.ConnectAsync(account, cancellationToken);
        var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
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
        await client.DisconnectAsync(true, cancellationToken);

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

        using var client = await _imapConnection.ConnectAsync(account, cancellationToken);
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
        await client.DisconnectAsync(true, cancellationToken);

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
            using var client = await _imapConnection.ConnectAsync(account, cancellationToken);
            var imapFolder = await client.GetFolderAsync(trashFolder.FullName, cancellationToken);
            await imapFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

            var uid = new UniqueId(message.Uid);
            await imapFolder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken);
            await imapFolder.ExpungeAsync(cancellationToken);

            _dbContext.Messages.Remove(message);
            await _dbContext.SaveChangesAsync(cancellationToken);

            await imapFolder.CloseAsync(false, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.Information("Permanently deleted message UID {Uid} from Trash for {Email}",
                message.Uid, account.EmailAddress);
            return;
        }

        await MoveMessageAsync(account, message, trashFolder, cancellationToken);
        _logger.Information("Moved message UID {Uid} to Trash for {Email}", message.Uid, account.EmailAddress);
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

            // Fetch full message body
            try
            {
                var mimeMessage = await imapFolder.GetMessageAsync(summary.UniqueId, cancellationToken);
                emailMessage.BodyText = mimeMessage.TextBody;
                emailMessage.BodyHtml = mimeMessage.HtmlBody;
                if (mimeMessage.References != null && mimeMessage.References.Count > 0)
                    emailMessage.References = string.Join(" ", mimeMessage.References);

                // Cache attachment metadata
                foreach (var attachment in mimeMessage.Attachments.OfType<MimePart>())
                {
                    emailMessage.Attachments.Add(new EmailAttachment
                    {
                        FileName = attachment.FileName ?? "unnamed",
                        ContentType = attachment.ContentType?.MimeType,
                        Size = attachment.Content?.Stream?.Length ?? 0,
                        ContentId = attachment.ContentId
                    });
                }

                emailMessage.HasAttachments = emailMessage.Attachments.Count > 0;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to fetch body for UID {Uid} in {Folder}", summary.UniqueId.Id, localFolder.FullName);
            }

            _dbContext.Messages.Add(emailMessage);
            processedInBatch++;

            if (processedInBatch >= batchSize)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                processedInBatch = 0;
            }
        }

        if (processedInBatch > 0)
            await _dbContext.SaveChangesAsync(cancellationToken);
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
            var deletedMessages = await _dbContext.Messages
                .Where(m => m.FolderId == localFolder.Id && deletedUids.Contains(m.Uid))
                .ToListAsync(cancellationToken);

            _dbContext.Messages.RemoveRange(deletedMessages);
            _logger.Information("Detected {Count} deleted messages in {Folder}", deletedUids.Count, localFolder.FullName);
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
