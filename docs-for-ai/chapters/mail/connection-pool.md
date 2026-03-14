# ImapConnectionPool — Per-account IMAP connection reuse

## Overview

Maintains a pool of authenticated `ImapClient` instances keyed by `Account.Id`, avoiding repeated TCP+TLS+AUTH handshakes for UI operations. Returns `PooledImapConnection` wrappers whose `Dispose()` returns the connection to the pool instead of disconnecting. A background timer cleans up stale connections every 5 minutes.

## Key Behaviors

- **Max 2 connections per account**: Enforced via atomic `_poolCounts` increment in `ReturnToPool`. Excess connections are disposed, not queued.
- **OAuth token expiry check**: On dequeue, discards pooled connections whose `OAuthTokenExpiry <= UtcNow`. Forces fresh `ConnectAsync` which triggers token refresh.
- **Stale connection cleanup**: `CleanupStaleConnections()` runs on a 5-minute `Timer`. Dequeues all connections, re-enqueues only those still connected + authenticated, disposes the rest.
- **TOCTOU-safe pool sizing**: Uses `ConcurrentDictionary<int, int>` for atomic count tracking rather than checking `ConcurrentQueue.Count` (which races).
- **Account removal**: `RemoveAccount(int)` disposes all pooled connections and removes the account key. Called by `AccountService` on account delete.
- **PooledImapConnection**: Sealed wrapper exposing `Client` property. `Dispose()` calls the pool's `ReturnToPool` action. Internal constructor prevents external instantiation.

## Interface

`IImapConnectionPool : IDisposable`
- `GetConnectionAsync(Account, CancellationToken)` — returns `PooledImapConnection`
- `RemoveAccount(int accountId)` — drain + dispose all connections for account

## Internal Details

```
ConcurrentDictionary<int, ConcurrentQueue<ImapClient>> _pool     // account → queue
ConcurrentDictionary<int, int>                         _poolCounts // account → current count
Timer                                                  _cleanupTimer
```

`ReturnToPool` flow:
1. If pool disposed or client disconnected → dispose client
2. Atomic increment `_poolCounts[accountId]`
3. If count <= `MaxPoolSizePerAccount` (2) → enqueue
4. Else → decrement back, dispose client

`DisposeClient` disconnects gracefully (`Disconnect(true)`) then disposes, swallowing exceptions.

`Dispose()` sets `_disposed = true`, stops timer, drains all queues.

## Dependencies

- Uses: `IImapConnectionService` (creates new connections when pool empty)
- Used by: `EmailSyncService`, `AccountService` (cleanup on delete)
