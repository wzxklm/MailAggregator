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
    /// Reads the folder list for the account from the local database (no IMAP connection).
    /// Returns empty list if no folders have been synced yet.
    /// </summary>
    Task<IReadOnlyList<MailFolder>> GetFoldersFromDbAsync(int accountId, CancellationToken cancellationToken = default);

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
}
