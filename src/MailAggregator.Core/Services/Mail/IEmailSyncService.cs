using MailAggregator.Core.Models;
using MailKit.Net.Imap;

namespace MailAggregator.Core.Services.Mail;

public interface IEmailSyncService
{
    /// <summary>
    /// Fetches the IMAP folder list for the account, recognizing SPECIAL-USE attributes.
    /// Syncs folder metadata to local database. Creates and disposes its own IMAP connection.
    /// </summary>
    Task<IReadOnlyList<MailFolder>> SyncFoldersAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the IMAP folder list using a caller-provided IMAP client.
    /// The caller is responsible for connecting and disconnecting the client.
    /// </summary>
    Task<IReadOnlyList<MailFolder>> SyncFoldersAsync(Account account, ImapClient client, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs initial sync: pulls envelope info and body for emails from the last 30 days.
    /// </summary>
    Task SyncInitialAsync(Account account, MailFolder folder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs incremental sync based on UIDVALIDITY + max UID.
    /// Creates and disposes its own IMAP connection.
    /// </summary>
    Task<int> SyncIncrementalAsync(Account account, MailFolder folder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs incremental sync using a caller-provided IMAP client.
    /// The caller is responsible for connecting and disconnecting the client.
    /// Returns the number of new messages synced.
    /// </summary>
    Task<int> SyncIncrementalAsync(Account account, MailFolder folder, ImapClient client, CancellationToken cancellationToken = default);

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
