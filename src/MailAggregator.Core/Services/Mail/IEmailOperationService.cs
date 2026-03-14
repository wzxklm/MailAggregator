using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.Mail;

public interface IEmailOperationService
{
    /// <summary>
    /// Sets or clears the \Seen flag on a message on the IMAP server and updates local cache.
    /// </summary>
    Task SetMessageReadAsync(Account account, EmailMessage message, bool isRead, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads an attachment to the specified local path.
    /// </summary>
    Task DownloadAttachmentAsync(Account account, EmailMessage message, EmailAttachment attachment, string savePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a message to the specified destination folder on the IMAP server and updates local cache.
    /// </summary>
    Task MoveMessageAsync(Account account, EmailMessage message, MailFolder destinationFolder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a message by moving it to the Trash folder (identified by \Trash SPECIAL-USE).
    /// </summary>
    Task DeleteMessageAsync(Account account, EmailMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the full message body and attachment metadata from IMAP for a message
    /// whose body was not cached during sync. Updates the local database.
    /// </summary>
    Task FetchMessageBodyAsync(Account account, EmailMessage message, CancellationToken cancellationToken = default);
}
