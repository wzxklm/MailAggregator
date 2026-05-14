# Sync — Background IMAP IDLE sync orchestration

## Files

| File | Responsibility |
|------|----------------|
| `src/MailAggregator.Core/Services/Sync/ISyncManager.cs` | Interface + event args (`NewEmailsEventArgs`, `FoldersSyncedEventArgs`) |
| `src/MailAggregator.Core/Services/Sync/SyncManager.cs` | Implementation: per-account sync loops, IDLE/polling, backoff, network awareness |

## SyncManager

### Overview

Orchestrates continuous background IMAP sync for all accounts. Each account gets its own long-running `Task` that connects via IMAP, performs an initial incremental sync, then enters an IDLE (or polling) watch loop to detect new mail. Handles reconnection with exponential backoff + jitter, network-down pausing, and graceful shutdown.

### Key Behaviors

- **Per-account sync loop**: `StartAccountSyncAsync` spawns a `Task.Run` loop per account, tracked in a `ConcurrentDictionary<int, (Task, CancellationTokenSource)>` keyed by `Account.Id`
- **Duplicate prevention**: `GetOrAdd` on the dictionary atomically prevents TOCTOU races when starting sync twice for the same account
- **IDLE with automatic polling fallback**: Prefers IMAP IDLE (RFC 2177) if server supports it and `Account.UseIdle` is true. Falls back to polling (`PollingInterval = 59s`) otherwise. After `MaxIdleFailuresBeforePolling` (2) consecutive IDLE rejections, permanently switches to polling for that session
- **IDLE cycle**: 29-minute timeout (`IdleTimeout`), re-enters after timeout. Subscribes to `IMailFolder.CountChanged` to cancel IDLE immediately when server pushes EXISTS notification
- **STATUS over NOOP**: Uses `StatusAsync(StatusItems.Count)` after each IDLE/poll cycle instead of NOOP — some servers (163.com, QQ Mail) treat NOOP-only sessions as idle and auto-logout
- **Incremental sync on change**: When `imapInbox.Count` increases, calls `IEmailSyncService.SyncIncrementalAsync`, then raises `NewEmailsReceived` event
- **Folder sync on first connect**: If no folders in DB, calls `SyncFoldersAsync` and raises `FoldersSynced` event
- **Exponential backoff with jitter**: `CalculateBackoffDelay(attempt)` — base = `1s * 2^attempt`, capped at `300s`, jittered ±25% to prevent thundering herd
- **Network awareness**: Listens to `NetworkChange.NetworkAvailabilityChanged`. When network drops, `ManualResetEventSlim` blocks sync loops from burning backoff cycles. When restored, resets backoff to 0 and reconnects immediately
- **Non-transient error handling**: `OAuthReauthenticationRequiredException` and non-transient IMAP auth errors (`MailConnectionHelper.IsNonTransientAuthError`) break the loop permanently — no retry

### Interface

`ISyncManager` — `StartAccountSyncAsync(Account, CancellationToken)`, `StopAccountSyncAsync(int accountId)`, `StopAllAsync()`, `IsAccountSyncing(int accountId)`

Events: `NewEmailsReceived`, `FoldersSynced`

### Internal Details

**Threading & concurrency mechanisms**:

| Mechanism | Field | Purpose |
|-----------|-------|---------|
| `ConcurrentDictionary<int, (Task, CTS)>` | `_runningSyncs` | Thread-safe registry of active sync loops; keyed by account ID; `GetOrAdd` for atomic start, `TryRemove` for stop |
| `ManualResetEventSlim` | `_networkAvailable` | Initialized signaled (`true`). Reset when network drops, Set when restored. Sync loops call `_networkAvailable.Wait(ct)` to block without spinning |
| `CancellationTokenSource` | per-account linked CTS | Created via `CreateLinkedTokenSource` so both caller cancellation and `StopAccountSyncAsync` cancel the loop |
| `CancellationTokenSource` | `idleDone` (local) | Per-IDLE-cycle; `CancelAfter(29min)` for timeout + cancelled by `CountChanged` event handler for immediate wakeup |

**StopAllAsync flow**: Snapshots all entries from `_runningSyncs` via `TryRemove`, cancels all CTS tokens, then `Task.WhenAll` awaits all sync tasks concurrently before disposing.

**Loop exit cleanup**: When the sync loop exits (cancellation, auth error, or no inbox), it calls `_runningSyncs.TryRemove` to clean up its own dictionary entry and dispose its CTS.

**Constants**:

| Constant | Value | Rationale |
|----------|-------|-----------|
| `IdleTimeout` | 29 min | RFC 2177 recommends < 30 min |
| `InitialReconnectDelay` | 1s | Fast first retry |
| `MaxReconnectDelay` | 300s | Cap for long offline scenarios |
| `PollingInterval` | 59s | Fallback when IDLE unavailable |
| `JitterFactor` | 0.25 | ±25% randomization |
| `MaxIdleFailuresBeforePolling` | 2 | Consecutive IDLE rejections before switch |

### Dependencies

- Uses: `IImapConnectionService` (connect), `IEmailSyncService` (folder sync, incremental sync), `MailConnectionHelper.IsNonTransientAuthError` (error classification), `Serilog.ILogger`
- Used by: `AccountService` (start/stop sync per account), `MainViewModel` (subscribes to `NewEmailsReceived` / `FoldersSynced` events)
