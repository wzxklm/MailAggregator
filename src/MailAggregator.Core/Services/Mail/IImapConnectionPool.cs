using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.Mail;

/// <summary>
/// Manages a pool of reusable IMAP connections per account.
/// Avoids creating a new TCP+TLS+AUTH handshake for every UI operation.
/// </summary>
public interface IImapConnectionPool : IDisposable
{
    /// <summary>
    /// Gets an IMAP connection from the pool (or creates a new one).
    /// Dispose the returned <see cref="PooledImapConnection"/> to return it to the pool.
    /// </summary>
    Task<PooledImapConnection> GetConnectionAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes and disposes all pooled connections for the specified account.
    /// </summary>
    void RemoveAccount(int accountId);
}
