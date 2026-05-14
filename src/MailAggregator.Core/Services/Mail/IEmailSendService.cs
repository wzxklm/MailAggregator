using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.Mail;

public interface IEmailSendService
{
    /// <summary>
    /// Composes and sends a new email.
    /// </summary>
    Task SendAsync(
        Account senderAccount,
        string to,
        string? cc,
        string? bcc,
        string subject,
        string body,
        bool isHtml,
        IReadOnlyList<string>? attachmentPaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replies to an email. Fills In-Reply-To, References headers and quotes the original text.
    /// </summary>
    Task ReplyAsync(
        Account senderAccount,
        EmailMessage originalMessage,
        string body,
        bool isHtml,
        IReadOnlyList<string>? attachmentPaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replies to all recipients of an email.
    /// </summary>
    Task ReplyAllAsync(
        Account senderAccount,
        EmailMessage originalMessage,
        string body,
        bool isHtml,
        IReadOnlyList<string>? attachmentPaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forwards an email with original attachments.
    /// </summary>
    Task ForwardAsync(
        Account senderAccount,
        EmailMessage originalMessage,
        string to,
        string? cc,
        string? bcc,
        string body,
        bool isHtml,
        IReadOnlyList<string>? additionalAttachmentPaths,
        CancellationToken cancellationToken = default);
}
