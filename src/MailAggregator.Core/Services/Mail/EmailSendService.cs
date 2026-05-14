using System.Net;
using MailAggregator.Core.Data;
using MailAggregator.Core.Models;
using MailKit;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Serilog;

namespace MailAggregator.Core.Services.Mail;

public class EmailSendService : IEmailSendService
{
    private readonly ISmtpConnectionService _smtpConnection;
    private readonly IImapConnectionService _imapConnection;
    private readonly MailAggregatorDbContext _dbContext;
    private readonly ILogger _logger;

    public EmailSendService(
        ISmtpConnectionService smtpConnection,
        IImapConnectionService imapConnection,
        MailAggregatorDbContext dbContext,
        ILogger logger)
    {
        _smtpConnection = smtpConnection ?? throw new ArgumentNullException(nameof(smtpConnection));
        _imapConnection = imapConnection ?? throw new ArgumentNullException(nameof(imapConnection));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SendAsync(
        Account senderAccount,
        string to,
        string? cc,
        string? bcc,
        string subject,
        string body,
        bool isHtml,
        IReadOnlyList<string>? attachmentPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(senderAccount);
        ArgumentException.ThrowIfNullOrEmpty(to);

        var message = new MimeMessage();
        message.From.Add(GetSenderAddress(senderAccount));
        SetRecipients(message, to, cc, bcc);
        message.Subject = subject;

        message.Body = BuildMessageBody(body, isHtml, attachmentPaths);

        await SendMessageAsync(senderAccount, message, cancellationToken);
    }

    public async Task ReplyAsync(
        Account senderAccount,
        EmailMessage originalMessage,
        string body,
        bool isHtml,
        IReadOnlyList<string>? attachmentPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(senderAccount);
        ArgumentNullException.ThrowIfNull(originalMessage);

        var message = new MimeMessage();
        message.From.Add(GetSenderAddress(senderAccount));
        message.To.Add(new MailboxAddress(originalMessage.FromName ?? originalMessage.FromAddress, originalMessage.FromAddress));

        message.Subject = originalMessage.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true
            ? originalMessage.Subject
            : $"Re: {originalMessage.Subject}";

        SetReplyHeaders(message, originalMessage);

        var quotedBody = BuildQuotedReply(originalMessage, body, isHtml);
        message.Body = BuildMessageBody(quotedBody, isHtml, attachmentPaths);

        await SendMessageAsync(senderAccount, message, cancellationToken);
    }

    public async Task ReplyAllAsync(
        Account senderAccount,
        EmailMessage originalMessage,
        string body,
        bool isHtml,
        IReadOnlyList<string>? attachmentPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(senderAccount);
        ArgumentNullException.ThrowIfNull(originalMessage);

        var message = new MimeMessage();
        message.From.Add(GetSenderAddress(senderAccount));

        // Reply to original sender
        message.To.Add(new MailboxAddress(originalMessage.FromName ?? originalMessage.FromAddress, originalMessage.FromAddress));

        // Add all original To recipients except the sender
        if (!string.IsNullOrEmpty(originalMessage.ToAddresses))
        {
            foreach (var addr in ParseAddresses(originalMessage.ToAddresses))
            {
                if (addr is MailboxAddress mbox && !string.Equals(mbox.Address, senderAccount.EmailAddress, StringComparison.OrdinalIgnoreCase))
                    message.To.Add(addr);
            }
        }

        // Add all original CC recipients
        if (!string.IsNullOrEmpty(originalMessage.CcAddresses))
        {
            foreach (var addr in ParseAddresses(originalMessage.CcAddresses))
            {
                if (addr is MailboxAddress mbox && !string.Equals(mbox.Address, senderAccount.EmailAddress, StringComparison.OrdinalIgnoreCase))
                    message.Cc.Add(addr);
            }
        }

        message.Subject = originalMessage.Subject?.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) == true
            ? originalMessage.Subject
            : $"Re: {originalMessage.Subject}";

        SetReplyHeaders(message, originalMessage);

        var quotedBody = BuildQuotedReply(originalMessage, body, isHtml);
        message.Body = BuildMessageBody(quotedBody, isHtml, attachmentPaths);

        await SendMessageAsync(senderAccount, message, cancellationToken);
    }

    public async Task ForwardAsync(
        Account senderAccount,
        EmailMessage originalMessage,
        string to,
        string? cc,
        string? bcc,
        string body,
        bool isHtml,
        IReadOnlyList<string>? additionalAttachmentPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(senderAccount);
        ArgumentNullException.ThrowIfNull(originalMessage);
        ArgumentException.ThrowIfNullOrEmpty(to);

        var message = new MimeMessage();
        message.From.Add(GetSenderAddress(senderAccount));
        SetRecipients(message, to, cc, bcc);

        message.Subject = originalMessage.Subject?.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase) == true
            ? originalMessage.Subject
            : $"Fwd: {originalMessage.Subject}";

        var forwardedBody = BuildForwardBody(originalMessage, body, isHtml);

        // Get original attachments from IMAP
        var allAttachmentPaths = new List<string>();
        if (additionalAttachmentPaths != null)
            allAttachmentPaths.AddRange(additionalAttachmentPaths);

        // Fetch original message attachments from server
        var originalMime = await FetchOriginalMessageAsync(senderAccount, originalMessage, cancellationToken);

        var bodyPart = BuildMessageBody(forwardedBody, isHtml, allAttachmentPaths);

        if (originalMime != null)
        {
            var multipart = bodyPart as Multipart ?? new Multipart("mixed") { bodyPart };
            foreach (var attachment in originalMime.Attachments.OfType<MimePart>())
            {
                multipart.Add(attachment);
            }
            message.Body = multipart;
        }
        else
        {
            message.Body = bodyPart;
        }

        await SendMessageAsync(senderAccount, message, cancellationToken);
    }

    private async Task SendMessageAsync(Account account, MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = await _smtpConnection.ConnectAsync(account, cancellationToken);

        await client.SendAsync(message, cancellationToken);
        _logger.Information("Sent email from {From} to {To}, subject: {Subject}",
            account.EmailAddress, message.To, message.Subject);

        await client.DisconnectAsync(true, cancellationToken);

        // Save a copy to the Sent folder (most IMAP servers don't do this automatically)
        await AppendToSentFolderAsync(account, message, cancellationToken);
    }

    private async Task AppendToSentFolderAsync(Account account, MimeMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var sentFolder = await _dbContext.Folders
                .FirstOrDefaultAsync(f => f.AccountId == account.Id && f.SpecialUse == SpecialFolderType.Sent, cancellationToken);

            if (sentFolder == null)
            {
                _logger.Warning("No Sent folder found for {Email}, skipping sent copy", account.EmailAddress);
                return;
            }

            using var imapClient = await _imapConnection.ConnectAsync(account, cancellationToken);
            var folder = await imapClient.GetFolderAsync(sentFolder.FullName, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            await folder.AppendAsync(message, MessageFlags.Seen, cancellationToken: cancellationToken);
            await folder.CloseAsync(false, cancellationToken);
            await imapClient.DisconnectAsync(true, cancellationToken);

            _logger.Information("Saved sent email to {Folder} for {Email}", sentFolder.FullName, account.EmailAddress);
        }
        catch (Exception ex)
        {
            // Don't throw — email was already sent successfully
            _logger.Warning(ex, "Failed to save sent email to Sent folder for {Email}", account.EmailAddress);
        }
    }

    private async Task<MimeMessage?> FetchOriginalMessageAsync(Account account, EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var folder = await _dbContext.Folders.FindAsync(new object[] { message.FolderId }, cancellationToken);
            if (folder == null) return null;

            using var client = await _imapConnection.ConnectAsync(account, cancellationToken);
            var imapFolder = await client.GetFolderAsync(folder.FullName, cancellationToken);
            await imapFolder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var uid = new UniqueId(message.Uid);
            var mimeMessage = await imapFolder.GetMessageAsync(uid, cancellationToken);

            await imapFolder.CloseAsync(false, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            return mimeMessage;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch original message for forwarding. UID {Uid}", message.Uid);
            return null;
        }
    }

    private static void SetReplyHeaders(MimeMessage reply, EmailMessage original)
    {
        if (!string.IsNullOrEmpty(original.MessageId))
        {
            reply.InReplyTo = original.MessageId;

            // Build References chain
            var references = new List<string>();
            if (!string.IsNullOrEmpty(original.References))
            {
                references.AddRange(original.References.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            references.Add(original.MessageId);

            foreach (var r in references)
                reply.References.Add(r);
        }
    }

    internal static string BuildQuotedReply(EmailMessage original, string replyBody, bool isHtml)
    {
        if (isHtml)
        {
            var quotedOriginal = !string.IsNullOrEmpty(original.BodyHtml) ? original.BodyHtml : WebUtility.HtmlEncode(original.BodyText ?? "");
            return $"{replyBody}<br/><br/>" +
                   $"<div>--- Original Message ---<br/>" +
                   $"From: {WebUtility.HtmlEncode(original.FromAddress)}<br/>" +
                   $"Date: {original.DateSent:yyyy-MM-dd HH:mm}<br/>" +
                   $"Subject: {WebUtility.HtmlEncode(original.Subject ?? "")}<br/><br/>" +
                   $"<blockquote style=\"border-left:2px solid #ccc;padding-left:10px;margin-left:0\">{quotedOriginal}</blockquote></div>";
        }
        else
        {
            var quotedOriginal = original.BodyText ?? "";
            var quotedLines = quotedOriginal.Split('\n').Select(l => $"> {l}");
            return $"{replyBody}\n\n" +
                   $"--- Original Message ---\n" +
                   $"From: {original.FromAddress}\n" +
                   $"Date: {original.DateSent:yyyy-MM-dd HH:mm}\n" +
                   $"Subject: {original.Subject}\n\n" +
                   string.Join("\n", quotedLines);
        }
    }

    internal static string BuildForwardBody(EmailMessage original, string forwardBody, bool isHtml)
    {
        if (isHtml)
        {
            var originalContent = !string.IsNullOrEmpty(original.BodyHtml) ? original.BodyHtml : WebUtility.HtmlEncode(original.BodyText ?? "");
            return $"{forwardBody}<br/><br/>" +
                   $"<div>--- Forwarded Message ---<br/>" +
                   $"From: {WebUtility.HtmlEncode(original.FromAddress)}<br/>" +
                   $"Date: {original.DateSent:yyyy-MM-dd HH:mm}<br/>" +
                   $"Subject: {WebUtility.HtmlEncode(original.Subject ?? "")}<br/>" +
                   $"To: {WebUtility.HtmlEncode(original.ToAddresses)}<br/><br/>" +
                   $"{originalContent}</div>";
        }
        else
        {
            return $"{forwardBody}\n\n" +
                   $"--- Forwarded Message ---\n" +
                   $"From: {original.FromAddress}\n" +
                   $"Date: {original.DateSent:yyyy-MM-dd HH:mm}\n" +
                   $"Subject: {original.Subject}\n" +
                   $"To: {original.ToAddresses}\n\n" +
                   (original.BodyText ?? "");
        }
    }

    internal static MimeEntity BuildMessageBody(string body, bool isHtml, IReadOnlyList<string>? attachmentPaths)
    {
        var textPart = isHtml
            ? new TextPart("html") { Text = body }
            : new TextPart("plain") { Text = body };

        if (attachmentPaths == null || attachmentPaths.Count == 0)
            return textPart;

        var multipart = new Multipart("mixed") { textPart };
        var openedStreams = new List<Stream>();

        try
        {
            foreach (var path in attachmentPaths)
            {
                var stream = File.OpenRead(path);
                openedStreams.Add(stream);
                var attachment = new MimePart(MimeTypes.GetMimeType(path))
                {
                    Content = new MimeContent(stream),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                    ContentTransferEncoding = ContentEncoding.Base64,
                    FileName = Path.GetFileName(path)
                };
                multipart.Add(attachment);
            }
        }
        catch
        {
            foreach (var stream in openedStreams)
                stream.Dispose();
            throw;
        }

        return multipart;
    }

    private static MailboxAddress GetSenderAddress(Account account)
    {
        return new MailboxAddress(account.DisplayName ?? account.EmailAddress, account.EmailAddress);
    }

    private static void SetRecipients(MimeMessage message, string to, string? cc, string? bcc)
    {
        message.To.AddRange(ParseAndValidateAddresses(to, "To"));
        if (!string.IsNullOrEmpty(cc)) message.Cc.AddRange(ParseAndValidateAddresses(cc, "Cc"));
        if (!string.IsNullOrEmpty(bcc)) message.Bcc.AddRange(ParseAndValidateAddresses(bcc, "Bcc"));
    }

    internal static InternetAddressList ParseAndValidateAddresses(string addresses, string fieldName)
    {
        if (!InternetAddressList.TryParse(addresses, out var list) || list.Count == 0)
            throw new ArgumentException($"{fieldName} contains no valid email addresses: {addresses}");

        var invalid = list.OfType<MailboxAddress>()
            .Where(m => !IsValidMailboxAddress(m))
            .Select(m => m.Address)
            .ToList();

        if (invalid.Count > 0)
            throw new ArgumentException($"Invalid email address(es) in {fieldName}: {string.Join(", ", invalid)}");

        return list;
    }

    internal static bool IsValidMailboxAddress(MailboxAddress mbox)
    {
        var address = mbox.Address;
        if (string.IsNullOrEmpty(address)) return false;
        var atIndex = address.IndexOf('@');
        return atIndex > 0 && atIndex < address.Length - 1;
    }

    private static IEnumerable<InternetAddress> ParseAddresses(string addresses)
    {
        if (InternetAddressList.TryParse(addresses, out var list))
            return list;
        return Enumerable.Empty<InternetAddress>();
    }

}
