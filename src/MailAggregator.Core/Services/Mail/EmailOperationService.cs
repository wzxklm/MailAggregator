using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailKit;
using MailKit.Net.Imap;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Serilog;

using LocalMailFolder = MailAggregator.Core.Models.MailFolder;

namespace MailAggregator.Core.Services.Mail;

public class EmailOperationService : IEmailOperationService
{
    private readonly IImapConnectionPool _connectionPool;
    private readonly IDbContextFactory<MailAggregatorDbContext> _dbContextFactory;
    private readonly ILogger _logger;

    public EmailOperationService(
        IImapConnectionPool connectionPool,
        IDbContextFactory<MailAggregatorDbContext> dbContextFactory,
        ILogger logger)
    {
        _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets a pooled connection and executes the action. Retries once on IOException
    /// (dead pooled connection whose TCP socket was silently dropped by firewall/NAT).
    /// </summary>
    private async Task WithPooledConnectionAsync(Account account, Func<ImapClient, Task> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var pooled = await _connectionPool.GetConnectionAsync(account, cancellationToken);
            try
            {
                await action(pooled.Client);
                return;
            }
            catch (IOException) when (attempt == 0)
            {
                // Dead pooled connection — let it be disposed, then retry with a fresh one
            }
        }
    }

    /// <summary>
    /// Gets a pooled connection and executes the action, returning its result. Retries once on IOException.
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

    public async Task SetMessageReadAsync(Account account, EmailMessage message, bool isRead, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);

        using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var folder = await dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} not found.");

        await WithPooledConnectionAsync(account, async client =>
        {
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
        }, cancellationToken);
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

        var mimeMessage = await WithPooledConnectionAsync(account, async client =>
        {
            var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var uid = new UniqueId(message.Uid);
            var mime = await imapFolder.GetMessageAsync(uid, cancellationToken);
            await imapFolder.CloseAsync(false, cancellationToken);
            return mime;
        }, cancellationToken);

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

        await WithPooledConnectionAsync(account, async client =>
        {
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
        }, cancellationToken);
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
            await WithPooledConnectionAsync(account, async client =>
            {
                var imapFolder = await client.GetFolderAsync(trashFolder.FullName, cancellationToken);
                await imapFolder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

                var uid = new UniqueId(message.Uid);
                await imapFolder.AddFlagsAsync(uid, MessageFlags.Deleted, true, cancellationToken);
                await imapFolder.ExpungeAsync(cancellationToken);

                dbContext.Messages.Remove(message);
                await dbContext.SaveChangesAsync(cancellationToken);

                await imapFolder.CloseAsync(false, cancellationToken);

                _logger.Information("Permanently deleted message UID {Uid} from Trash for {Email}",
                    message.Uid, account.EmailAddress);
            }, cancellationToken);
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

        var mimeMessage = await WithPooledConnectionAsync(account, async client =>
        {
            var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var uid = new UniqueId(message.Uid);
            return await imapFolder.GetMessageAsync(uid, cancellationToken);
        }, cancellationToken);

        message.BodyText = mimeMessage.TextBody;
        message.BodyHtml = mimeMessage.HtmlBody;

        // Replace cid: references with inline data URIs so WebView2 can display them
        if (!string.IsNullOrEmpty(message.BodyHtml))
        {
            message.BodyHtml = ResolveInlineImages(mimeMessage, message.BodyHtml);
        }

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

        _logger.Debug("Fetched body for UID {Uid} in {Folder} for {Email}",
            message.Uid, folder.FullName, account.EmailAddress);
    }

    /// <summary>
    /// Replaces cid: references in HTML with inline data URIs from the MIME message's body parts.
    /// This makes the HTML self-contained so WebView2 can render embedded images without
    /// needing to resolve Content-ID URIs.
    /// </summary>
    internal static string ResolveInlineImages(MimeMessage mimeMessage, string html)
    {
        var cidParts = mimeMessage.BodyParts.OfType<MimePart>()
            .Where(p => !string.IsNullOrEmpty(p.ContentId) && p.Content != null)
            .ToList();

        if (cidParts.Count == 0)
            return html;

        foreach (var part in cidParts)
        {
            var contentId = part.ContentId!.Trim('<', '>');
            var cidRef = $"cid:{contentId}";

            if (!html.Contains(cidRef, StringComparison.OrdinalIgnoreCase))
                continue;

            using var ms = new MemoryStream();
            part.Content!.DecodeTo(ms);
            var base64 = Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
            var mimeType = part.ContentType?.MimeType ?? "application/octet-stream";
            var dataUri = $"data:{mimeType};base64,{base64}";

            html = html.Replace(cidRef, dataUri, StringComparison.OrdinalIgnoreCase);
        }

        return html;
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
