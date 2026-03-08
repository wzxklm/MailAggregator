using System.Collections.Concurrent;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Mail;
using MailKit;
using MailKit.Net.Imap;
using Serilog;

using LocalAccount = MailAggregator.Core.Models.Account;
using LocalMailFolder = MailAggregator.Core.Models.MailFolder;

namespace MailAggregator.Core.Services.Sync;

public class SyncManager : ISyncManager
{
    /// <summary>
    /// Maximum time to stay in IMAP IDLE before re-entering (RFC 2177 recommends &lt; 30 min).
    /// </summary>
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(29);

    /// <summary>
    /// Initial reconnect delay after a connection failure.
    /// </summary>
    internal static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum reconnect delay (cap for exponential backoff).
    /// </summary>
    internal static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(60);

    private readonly IImapConnectionService _imapConnectionService;
    private readonly IEmailSyncService _emailSyncService;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<int, (Task SyncTask, CancellationTokenSource Cts)> _runningSyncs = new();

    public event EventHandler<NewEmailsEventArgs>? NewEmailsReceived;

    public SyncManager(
        IImapConnectionService imapConnectionService,
        IEmailSyncService emailSyncService,
        ILogger logger)
    {
        _imapConnectionService = imapConnectionService ?? throw new ArgumentNullException(nameof(imapConnectionService));
        _emailSyncService = emailSyncService ?? throw new ArgumentNullException(nameof(emailSyncService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAccountSyncAsync(LocalAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Atomically check-and-add using GetOrAdd to avoid TOCTOU race
        var added = false;
        _runningSyncs.GetOrAdd(account.Id, _ =>
        {
            added = true;
            var syncTask = Task.Run(() => AccountSyncLoopAsync(account, cts.Token), cts.Token);
            return (syncTask, cts);
        });

        if (!added)
        {
            cts.Dispose();
            _logger.Information("Sync already running for account {AccountId} ({Email}), skipping duplicate start",
                account.Id, account.EmailAddress);
            return Task.CompletedTask;
        }

        _logger.Information("Background sync started for account {AccountId} ({Email})",
            account.Id, account.EmailAddress);

        return Task.CompletedTask;
    }

    public async Task StopAccountSyncAsync(int accountId)
    {
        if (!_runningSyncs.TryRemove(accountId, out var entry))
        {
            _logger.Information("No running sync found for account {AccountId}", accountId);
            return;
        }

        _logger.Information("Stopping sync for account {AccountId}", accountId);
        entry.Cts.Cancel();

        try
        {
            await entry.SyncTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error while stopping sync for account {AccountId}", accountId);
        }
        finally
        {
            entry.Cts.Dispose();
        }

        _logger.Information("Sync stopped for account {AccountId}", accountId);
    }

    public async Task StopAllAsync()
    {
        _logger.Information("Stopping all background syncs ({Count} running)", _runningSyncs.Count);

        var entries = new List<(int AccountId, Task SyncTask, CancellationTokenSource Cts)>();

        // Snapshot and remove all entries
        foreach (var kvp in _runningSyncs)
        {
            if (_runningSyncs.TryRemove(kvp.Key, out var entry))
            {
                entries.Add((kvp.Key, entry.SyncTask, entry.Cts));
            }
        }

        // Cancel all tokens
        foreach (var entry in entries)
        {
            entry.Cts.Cancel();
        }

        // Await all tasks concurrently
        try
        {
            await Task.WhenAll(entries.Select(e => e.SyncTask)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Individual task exceptions logged below
        }

        // Dispose all CTS and log any non-cancellation errors
        foreach (var entry in entries)
        {
            if (entry.SyncTask.IsFaulted)
            {
                _logger.Error(entry.SyncTask.Exception?.InnerException,
                    "Error while stopping sync for account {AccountId}", entry.AccountId);
            }
            entry.Cts.Dispose();
        }

        _logger.Information("All background syncs stopped");
    }

    public bool IsAccountSyncing(int accountId)
    {
        return _runningSyncs.ContainsKey(accountId);
    }

    /// <summary>
    /// Calculates the exponential backoff delay for reconnection attempts.
    /// Delay = min(InitialReconnectDelay * 2^attempt, MaxReconnectDelay).
    /// </summary>
    internal static TimeSpan CalculateBackoffDelay(int attempt)
    {
        if (attempt < 0) throw new ArgumentOutOfRangeException(nameof(attempt));

        // Clamp the exponent to avoid overflow for very large attempt values
        var exponent = Math.Min(attempt, 30);
        var delayMs = InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedDelay = TimeSpan.FromMilliseconds(Math.Min(delayMs, MaxReconnectDelay.TotalMilliseconds));
        return cappedDelay;
    }

    private async Task AccountSyncLoopAsync(LocalAccount account, CancellationToken cancellationToken)
    {
        int reconnectAttempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            ImapClient? client = null;
            try
            {
                // Step 1: Connect
                _logger.Information("Connecting IMAP IDLE client for account {AccountId} ({Email})",
                    account.Id, account.EmailAddress);
                client = await _imapConnectionService.ConnectAsync(account, cancellationToken);

                // Step 2: Sync folders using the IDLE client (avoids extra connection)
                var folders = await _emailSyncService.SyncFoldersAsync(account, client, cancellationToken);
                var inbox = folders.FirstOrDefault(f => f.SpecialUse == SpecialFolderType.Inbox);

                if (inbox == null)
                {
                    _logger.Error("No Inbox folder found for account {AccountId} ({Email}). Cannot start IDLE.",
                        account.Id, account.EmailAddress);
                    return;
                }

                // Step 3: Initial incremental sync using the IDLE client (avoids extra connection)
                _logger.Information("Running incremental sync on Inbox for account {AccountId} ({Email})",
                    account.Id, account.EmailAddress);
                await _emailSyncService.SyncIncrementalAsync(account, inbox, client, cancellationToken);

                // Step 4: Open Inbox for IDLE (client is still connected, folder was closed by step 3)
                var imapInbox = await client.GetFolderAsync(inbox.FullName, cancellationToken);
                await imapInbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                var previousCount = imapInbox.Count;

                // IDLE setup succeeded; reset backoff
                reconnectAttempt = 0;

                // Step 5: IDLE loop
                while (!cancellationToken.IsCancellationRequested)
                {
                    _logger.Debug("Entering IMAP IDLE for account {AccountId} ({Email})",
                        account.Id, account.EmailAddress);

                    using var idleDone = new CancellationTokenSource();

                    // Set up timer to break IDLE before the 30-minute RFC limit
                    idleDone.CancelAfter(IdleTimeout);

                    try
                    {
                        await client.IdleAsync(idleDone.Token, cancellationToken);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // IDLE timeout expired or server sent notification - this is normal
                    }

                    // Check if we were asked to stop
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Step 6: Check for new messages
                    var currentCount = imapInbox.Count;
                    if (currentCount > previousCount)
                    {
                        var newCount = currentCount - previousCount;
                        _logger.Information("IDLE detected {NewCount} new message(s) for account {AccountId} ({Email})",
                            newCount, account.Id, account.EmailAddress);

                        // Run incremental sync using the IDLE client (avoids extra connection)
                        await _emailSyncService.SyncIncrementalAsync(account, inbox, client, cancellationToken);

                        // Raise event
                        OnNewEmailsReceived(new NewEmailsEventArgs(account.Id, account.EmailAddress, newCount));

                        // Reopen inbox for next IDLE iteration (SyncIncrementalAsync closed the folder)
                        await imapInbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
                    }

                    previousCount = imapInbox.Count;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Information("Sync loop cancelled for account {AccountId} ({Email})",
                    account.Id, account.EmailAddress);
                break;
            }
            catch (ImapCommandException ex) when (ex.Message.Contains("Unsafe Login", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("LOGIN", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Error(ex, "Non-transient auth/access error for account {AccountId} ({Email}). Stopping sync — check credentials or authorization code",
                    account.Id, account.EmailAddress);
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in sync loop for account {AccountId} ({Email}). Reconnect attempt {Attempt}",
                    account.Id, account.EmailAddress, reconnectAttempt);

                var delay = CalculateBackoffDelay(reconnectAttempt);
                reconnectAttempt++;

                _logger.Information("Reconnecting in {Delay}s for account {AccountId} ({Email})",
                    delay.TotalSeconds, account.Id, account.EmailAddress);

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (client != null)
                {
                    try
                    {
                        if (client.IsConnected)
                            await client.DisconnectAsync(true);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Error disconnecting IMAP client for account {AccountId}", account.Id);
                    }

                    client.Dispose();
                }
            }
        }

        // Clean up dictionary entry when loop exits naturally
        if (_runningSyncs.TryRemove(account.Id, out var self))
        {
            self.Cts.Dispose();
        }

        _logger.Information("Sync loop exited for account {AccountId} ({Email})",
            account.Id, account.EmailAddress);
    }

    /// <summary>
    /// Raises the <see cref="NewEmailsReceived"/> event.
    /// </summary>
    protected virtual void OnNewEmailsReceived(NewEmailsEventArgs e)
    {
        NewEmailsReceived?.Invoke(this, e);
    }
}
