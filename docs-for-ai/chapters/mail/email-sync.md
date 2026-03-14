# EmailSyncService — Folder discovery, message sync, and IMAP operations

## Overview

Handles all read-side IMAP operations: discovering server folders, syncing message envelopes (initial + incremental), syncing flags, detecting deletions, setting read status, downloading attachments, moving/deleting messages, and fetching full message bodies on demand. Uses `ImapConnectionPool` for connection reuse and per-folder semaphores to prevent concurrent sync on the same folder.

## Key Behaviors

- **Thunderbird-style folder discovery**: 4-strategy cascade in `DiscoverFoldersAsync`:
  1. Standard `PersonalNamespaces[0]` enumeration
  2. Default namespace (`""`, `"/"`) construction
  3. Root folder `GetFolder("")` + recursive subfolder collection (max depth 10)
  4. Last resort: INBOX only (RFC 3501 guarantee)
  Always ensures INBOX is included via `EnsureInboxIncluded`.
- **MailKit FolderCache injection**: For servers returning `NAMESPACE NIL`, injects a root folder into MailKit's internal `FolderCache` via reflection (`TryInjectRootFolderCache`). Also adds synthetic `PersonalNamespace`. Targets MailKit 4.15.1 internals (`ImapEngine.FolderCache`, `CreateImapFolder`, `CacheFolder`).
- **Initial sync**: Fetches envelopes for messages from last 30 days (`InitialSyncDays`). Bodies are NOT fetched — lazy-loaded via `FetchMessageBodyAsync` when user opens email.
- **Incremental sync**: Fetches UIDs > `folder.MaxUid`. Refreshes `MaxUid` from DB before querying to handle concurrent sync callers. If `UidValidity` changed, resets cache and falls back to initial sync.
- **Flag sync + deletion detection**: `SyncFlagsAndDetectDeletionsAsync` does a single IMAP FETCH of all local UIDs. UIDs in response get flag updates; UIDs absent from response are deleted locally.
- **Per-folder locking**: `ConcurrentDictionary<int, SemaphoreSlim>` prevents concurrent sync on same folder, avoiding UNIQUE constraint violations on `(FolderId, Uid)`.
- **Pooled connection retry**: `WithPooledConnectionAsync` retries once on `IOException` (dead pooled TCP connection silently dropped by firewall/NAT).
- **Batch saves**: `FetchAndCacheMessagesAsync` saves in batches of 50 to limit memory pressure.
- **Safe saves**: `SaveChangesSafeAsync` handles `DbUpdateConcurrencyException` (detach stale entries + retry) and SQLite UNIQUE constraint violations (detach duplicate Added entries + retry).
- **Inline image resolution**: `ResolveInlineImages` replaces `cid:` references in HTML with `data:` URIs so WebView2 renders embedded images without external resource resolution.
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
- `SetMessageReadAsync(Account, EmailMessage, bool, CancellationToken)` — set/clear `\Seen` flag
- `DownloadAttachmentAsync(Account, EmailMessage, EmailAttachment, string savePath, CancellationToken)`
- `MoveMessageAsync(Account, EmailMessage, MailFolder dest, CancellationToken)`
- `DeleteMessageAsync(Account, EmailMessage, CancellationToken)` — move to Trash, or expunge if already in Trash
- `FetchMessageBodyAsync(Account, EmailMessage, CancellationToken)` — lazy body + attachment metadata fetch

## Internal Details

Constants:
- `InitialSyncDays = 30`
- `MaxFolderDepth = 10` (recursive subfolder collection limit)
- Batch size = 50 (message insert batches)

`FetchAndCacheMessagesAsync` fetches `UniqueId | Envelope | Flags | BodyStructure | PreviewText` summaries. Maps `FolderAttributes` to `SpecialFolderType` via `MapSpecialUse`. Skips `NoSelect` folders during folder sync.

`DeleteMessageAsync` flow:
1. Find Trash folder by `SpecialFolderType.Trash`
2. If message already in Trash → add `\Deleted` flag + `Expunge` + remove from DB
3. Else → `MoveMessageAsync` to Trash

## Dependencies

- Uses: `IImapConnectionPool`, `IDbContextFactory<MailAggregatorDbContext>`, `MailConnectionHelper` (non-transient auth check)
- Used by: `SyncManager`, `MainViewModel`
