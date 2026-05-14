using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using MailAggregator.Core.Models;
using MailAggregator.Core.Services.Auth;
using MailAggregator.Core.Services.Mail;
using MailKit;
using MailKit.Net.Imap;
using Serilog;

using LocalAccount = MailAggregator.Core.Models.Account;
using LocalMailFolder = MailAggregator.Core.Models.MailFolder;

namespace MailAggregator.Core.Services.Sync;

public class SyncManager : ISyncManager, IDisposable
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
    /// Raised to 300s for long offline scenarios to reduce battery/resource waste.
    /// </summary>
    internal static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Polling interval used when the server does not support IMAP IDLE.
    /// </summary>
    internal static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(59);

    /// <summary>
    /// Jitter factor: backoff delay is randomized within [delay * (1 - factor), delay * (1 + factor)]
    /// to prevent thundering herd when multiple accounts reconnect simultaneously.
    /// </summary>
    internal const double JitterFactor = 0.25;

    /// <summary>
    /// Number of consecutive IDLE rejections before permanently switching to polling for the session.
    /// </summary>
    internal const int MaxIdleFailuresBeforePolling = 2;

    private readonly IImapConnectionService _imapConnectionService;
    private readonly IEmailSyncService _emailSyncService;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<int, (Task SyncTask, CancellationTokenSource Cts)> _runningSyncs = new();

    /// <summary>
    /// Signaled when network becomes available again, allowing sync loops
    /// to skip remaining backoff delay and reconnect immediately.
    /// </summary>
    private readonly ManualResetEventSlim _networkAvailable = new(true);

    public event EventHandler<NewEmailsEventArgs>? NewEmailsReceived;
    public event EventHandler<FoldersSyncedEventArgs>? FoldersSynced;

    public SyncManager(
        IImapConnectionService imapConnectionService,
        IEmailSyncService emailSyncService,
        ILogger logger)
    {
        _imapConnectionService = imapConnectionService ?? throw new ArgumentNullException(nameof(imapConnectionService));
        _emailSyncService = emailSyncService ?? throw new ArgumentNullException(nameof(emailSyncService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
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
    /// Calculates the exponential backoff delay with jitter for reconnection attempts.
    /// Base delay = min(InitialReconnectDelay * 2^attempt, MaxReconnectDelay),
    /// then randomized within ±25% to prevent thundering herd.
    /// </summary>
    internal static TimeSpan CalculateBackoffDelay(int attempt)
    {
        if (attempt < 0) throw new ArgumentOutOfRangeException(nameof(attempt));

        // Clamp the exponent to avoid overflow for very large attempt values
        var exponent = Math.Min(attempt, 30);
        var delayMs = InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(delayMs, MaxReconnectDelay.TotalMilliseconds);

        // Add jitter: randomize within [delay * 0.75, delay * 1.25]
        var jitter = (Random.Shared.NextDouble() * 2 - 1) * JitterFactor; // [-0.25, +0.25]
        var jitteredMs = cappedMs * (1 + jitter);

        return TimeSpan.FromMilliseconds(Math.Max(jitteredMs, InitialReconnectDelay.TotalMilliseconds));
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

                // Step 2: Load folders from DB; only sync from IMAP on first connection (no folders yet)
                var folders = await _emailSyncService.GetFoldersFromDbAsync(account.Id, cancellationToken);
                if (folders.Count == 0)
                {
                    folders = await _emailSyncService.SyncFoldersAsync(account, client, cancellationToken);
                    OnFoldersSynced(new FoldersSyncedEventArgs(account.Id));
                }
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
                var initialNewCount = await _emailSyncService.SyncIncrementalAsync(account, inbox, client, cancellationToken);

                if (initialNewCount > 0)
                {
                    OnNewEmailsReceived(new NewEmailsEventArgs(account.Id, account.EmailAddress, initialNewCount));
                }

                // Step 4: Open Inbox and determine watch strategy
                var imapInbox = await client.GetFolderAsync(inbox.FullName, cancellationToken);
                await imapInbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                var previousCount = imapInbox.Count;
                var supportsIdle = account.UseIdle && client.Capabilities.HasFlag(ImapCapabilities.Idle);

                if (!account.UseIdle)
                {
                    _logger.Information("IDLE disabled by user for account {AccountId} ({Email}), using polling (interval={Interval}s)",
                        account.Id, account.EmailAddress, PollingInterval.TotalSeconds);
                }
                else if (!supportsIdle)
                {
                    _logger.Information("Server does not support IDLE for account {AccountId} ({Email}), using polling (interval={Interval}s)",
                        account.Id, account.EmailAddress, PollingInterval.TotalSeconds);
                }

                // Connection setup succeeded; reset backoff
                reconnectAttempt = 0;
                var idleFailures = 0;

                // Step 5: Watch loop (IDLE or polling fallback)
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (supportsIdle)
                    {
                        var idleOk = await IdleWaitAsync(client, imapInbox, account, cancellationToken);
                        if (!idleOk)
                        {
                            idleFailures++;
                            if (idleFailures >= MaxIdleFailuresBeforePolling)
                            {
                                supportsIdle = false;
                                _logger.Information("IDLE rejected {Count} times for account {AccountId} ({Email}), permanently switching to polling (interval={Interval}s)",
                                    idleFailures, account.Id, account.EmailAddress, PollingInterval.TotalSeconds);
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(PollingInterval, cancellationToken);
                    }

                    // Check if we were asked to stop
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    // Step 6: STATUS refreshes folder state and resets the server-side idle timer.
                    // Using STATUS instead of NOOP because some servers (e.g. 163.com) treat
                    // NOOP-only sessions as idle and auto-logout after a few minutes, whereas
                    // STATUS is a real mail operation that resets their idle timer.
                    // For IDLE servers that don't push EXISTS reliably (e.g. QQ Mail), STATUS
                    // also provides an authoritative message count.
                    await imapInbox.StatusAsync(StatusItems.Count, cancellationToken);

                    var currentCount = imapInbox.Count;
                    if (currentCount > previousCount)
                    {
                        var newCount = currentCount - previousCount;
                        _logger.Information("{Mode} detected {NewCount} new message(s) for account {AccountId} ({Email})",
                            supportsIdle ? "IDLE" : "Poll", newCount, account.Id, account.EmailAddress);

                        await _emailSyncService.SyncIncrementalAsync(account, inbox, client, cancellationToken);
                        OnNewEmailsReceived(new NewEmailsEventArgs(account.Id, account.EmailAddress, newCount));

                        // Reopen inbox for next iteration (SyncIncrementalAsync closed the folder)
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
            catch (OAuthReauthenticationRequiredException ex)
            {
                _logger.Error(ex, "OAuth token revoked/expired for account {AccountId} ({Email}). Stopping sync — user must re-authenticate",
                    account.Id, account.EmailAddress);
                break;
            }
            catch (ImapCommandException ex) when (MailConnectionHelper.IsNonTransientAuthError(ex))
            {
                _logger.Error(ex, "Non-transient auth/access error for account {AccountId} ({Email}). Stopping sync — check credentials or authorization code",
                    account.Id, account.EmailAddress);
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in sync loop for account {AccountId} ({Email}). Reconnect attempt {Attempt}",
                    account.Id, account.EmailAddress, reconnectAttempt);

                // If network is down, wait for it to come back before consuming backoff cycles
                if (!_networkAvailable.IsSet)
                {
                    _logger.Information("Network unavailable, waiting for connectivity before reconnecting account {AccountId} ({Email})",
                        account.Id, account.EmailAddress);

                    try
                    {
                        await Task.Run(() => _networkAvailable.Wait(cancellationToken), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Network is back — reset backoff so we reconnect quickly
                    reconnectAttempt = 0;
                    _logger.Information("Network restored, reconnecting immediately for account {AccountId} ({Email})",
                        account.Id, account.EmailAddress);
                    continue;
                }

                var delay = CalculateBackoffDelay(reconnectAttempt);
                reconnectAttempt++;

                _logger.Information("Reconnecting in {Delay:F1}s for account {AccountId} ({Email})",
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
    /// Executes a single IDLE wait cycle. Returns false if the server rejected the
    /// IDLE command (BAD/NO response), true otherwise.
    /// </summary>
    private async Task<bool> IdleWaitAsync(ImapClient client, IMailFolder imapInbox, LocalAccount account, CancellationToken cancellationToken)
    {
        _logger.Debug("Entering IMAP IDLE for account {AccountId} ({Email})",
            account.Id, account.EmailAddress);

        using var idleDone = new CancellationTokenSource();
        idleDone.CancelAfter(IdleTimeout);

        // Cancel IDLE immediately when the server pushes a new-mail notification (EXISTS).
        // Without this, IdleAsync blocks until the 29-minute timeout even if mail arrives.
        void OnCountChanged(object? sender, EventArgs e) => idleDone.Cancel();
        imapInbox.CountChanged += OnCountChanged;

        try
        {
            await client.IdleAsync(idleDone.Token, cancellationToken);
            return true;
        }
        catch (ImapCommandException ex)
        {
            _logger.Warning(ex, "IDLE rejected by server for account {AccountId} ({Email})",
                account.Id, account.EmailAddress);
            await Task.Delay(PollingInterval, cancellationToken);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // IDLE timeout expired or server sent notification — this is normal
            return true;
        }
        finally
        {
            imapInbox.CountChanged -= OnCountChanged;
        }
    }

    /// <summary>
    /// Raises the <see cref="NewEmailsReceived"/> event.
    /// </summary>
    protected virtual void OnNewEmailsReceived(NewEmailsEventArgs e)
    {
        NewEmailsReceived?.Invoke(this, e);
    }

    /// <summary>
    /// Raises the <see cref="FoldersSynced"/> event.
    /// </summary>
    protected virtual void OnFoldersSynced(FoldersSyncedEventArgs e)
    {
        FoldersSynced?.Invoke(this, e);
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
        {
            _logger.Information("Network connectivity restored — signaling sync loops to reconnect");
            _networkAvailable.Set();
        }
        else
        {
            _logger.Warning("Network connectivity lost — sync loops will pause until restored");
            _networkAvailable.Reset();
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _networkAvailable.Dispose();
    }
}
