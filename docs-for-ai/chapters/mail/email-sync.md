# EmailSyncService — Folder sync, initial/incremental message sync

## Overview

Handles folder synchronization and message sync operations: syncing the server folder tree to local DB, performing initial sync (last 30 days), incremental sync (new UIDs), flag sync, and deletion detection. Uses `ImapConnectionPool` for connection reuse, `ImapFolderDiscovery` for folder discovery, and per-folder semaphores to prevent concurrent sync on the same folder. Single-message operations (read, delete, move, download, fetch body) are in `EmailOperationService`.

## Key Behaviors

- **Folder sync**: `SyncFoldersCoreAsync` delegates folder discovery to `ImapFolderDiscovery`, then upserts local `MailFolder` entities. Removes local folders no longer on server. Skips `NoSelect` container folders.
- **Initial sync**: Fetches envelopes for messages from last 30 days (`InitialSyncDays`). Bodies are NOT fetched — lazy-loaded via `EmailOperationService.FetchMessageBodyAsync` when user opens email.
- **Incremental sync**: Fetches UIDs > `folder.MaxUid`. Refreshes `MaxUid` from DB before querying to handle concurrent sync callers. If `UidValidity` changed, resets cache and falls back to initial sync.
- **Flag sync + deletion detection**: `SyncFlagsAndDetectDeletionsAsync` does a single IMAP FETCH of all local UIDs. UIDs in response get flag updates; UIDs absent from response are deleted locally.
- **Per-folder locking**: `ConcurrentDictionary<int, SemaphoreSlim>` prevents concurrent sync on same folder, avoiding UNIQUE constraint violations on `(FolderId, Uid)`.
- **Pooled connection retry**: `WithPooledConnectionAsync` retries once on `IOException` (dead pooled TCP connection silently dropped by firewall/NAT).
- **Batch saves**: `FetchAndCacheMessagesAsync` saves in batches of 50 to limit memory pressure.
- **Safe saves**: `SaveChangesSafeAsync` handles `DbUpdateConcurrencyException` (detach stale entries + retry) and SQLite UNIQUE constraint violations (detach duplicate Added entries + retry).
- **Non-selectable folder handling**: During incremental sync, `ImapCommandResponse.No` on folder open either re-throws (auth error) or removes the folder from local DB.
- **EF Core attach pattern**: Uses `Attach` + mark-modified on specific properties instead of `Update` to avoid graph traversal cascading to navigation properties.

## Interface

`IEmailSyncService`
- `SyncFoldersAsync(Account, CancellationToken)` — pooled connection
- `SyncFoldersAsync(Account, ImapClient, CancellationToken)` — caller-provided client
- `GetFoldersFromDbAsync(int accountId, CancellationToken)` — local DB only, no IMAP
- `SyncInitialAsync(Account, MailFolder, CancellationToken)` — last 30 days
- `SyncIncrementalAsync(Account, MailFolder, CancellationToken)` — pooled connection, returns new count
- `SyncIncrementalAsync(Account, MailFolder, ImapClient, CancellationToken)` — caller-provided client

## Internal Details

Constants:
- `InitialSyncDays = 30`
- Batch size = 50 (message insert batches)

`FetchAndCacheMessagesAsync` fetches `UniqueId | Envelope | Flags | BodyStructure | PreviewText` summaries. Maps `FolderAttributes` to `SpecialFolderType` via `MapSpecialUse`. Skips `NoSelect` folders during folder sync.

## Dependencies

- Uses: `IImapConnectionPool`, `IDbContextFactory<MailAggregatorDbContext>`, `ImapFolderDiscovery` (internal), `MailConnectionHelper` (non-transient auth check)
- Used by: `SyncManager`, `MainViewModel`
