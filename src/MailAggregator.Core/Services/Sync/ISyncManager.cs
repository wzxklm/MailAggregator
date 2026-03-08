using MailAggregator.Core.Models;

namespace MailAggregator.Core.Services.Sync;

/// <summary>
/// Manages background IMAP IDLE sync for all enabled accounts.
/// </summary>
public interface ISyncManager
{
    /// <summary>
    /// Starts background sync for an account (runs IMAP IDLE + periodic incremental sync).
    /// </summary>
    Task StartAccountSyncAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops sync for a specific account.
    /// </summary>
    Task StopAccountSyncAsync(int accountId);

    /// <summary>
    /// Stops all running syncs (for application shutdown).
    /// </summary>
    Task StopAllAsync();

    /// <summary>
    /// Checks whether a given account is currently syncing.
    /// </summary>
    bool IsAccountSyncing(int accountId);

    /// <summary>
    /// Raised when new emails arrive during background sync.
    /// </summary>
    event EventHandler<NewEmailsEventArgs>? NewEmailsReceived;
}

/// <summary>
/// Event args for new email arrival notifications.
/// </summary>
public class NewEmailsEventArgs : EventArgs
{
    public int AccountId { get; }
    public string AccountEmail { get; }
    public int NewMessageCount { get; }

    public NewEmailsEventArgs(int accountId, string accountEmail, int newMessageCount)
    {
        AccountId = accountId;
        AccountEmail = accountEmail;
        NewMessageCount = newMessageCount;
    }
}
