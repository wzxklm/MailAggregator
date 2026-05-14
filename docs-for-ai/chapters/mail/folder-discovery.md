# ImapFolderDiscovery — Thunderbird-style 4-strategy IMAP folder discovery

## Overview

Internal helper class used by `EmailSyncService` to discover IMAP folders via a multi-strategy cascade. Designed to handle non-compliant IMAP servers that return incomplete or NIL NAMESPACE responses. Includes a reflection-based workaround to inject folders into MailKit's internal cache.

## Key Behaviors

- **4-strategy cascade** in `DiscoverFoldersAsync`:
  1. Standard `PersonalNamespaces[0]` enumeration (compliant servers)
  2. Default namespace (`""`, `"/"`) construction (servers with empty NAMESPACE)
  3. Root folder `GetFolder("")` + recursive subfolder collection (max depth 10)
  4. Last resort: INBOX only (RFC 3501 §5.1 guarantee)
  Always ensures INBOX is included via `EnsureInboxIncluded`.
- **MailKit FolderCache injection**: For servers returning `NAMESPACE NIL`, `TryInjectRootFolderCache` injects a root folder into MailKit's internal `FolderCache` via reflection. Also adds synthetic `PersonalNamespace`. Targets MailKit 4.15.1 internals (`ImapEngine.FolderCache`, `CreateImapFolder`, `CacheFolder`). Falls back silently on any reflection failure.
- **Directory separator detection**: `GetDirectorySeparator` reads `Inbox.DirectorySeparator`, falls back to `'/'` if null char.
- **Max depth guard**: Recursive subfolder collection capped at 10 levels to prevent infinite loops.

## Interface

`internal class ImapFolderDiscovery` (no public interface — used only by `EmailSyncService`)
- `DiscoverFoldersAsync(Account, ImapClient, CancellationToken)` — returns discovered folders
- `TryInjectRootFolderCache(ImapClient)` — reflection-based cache injection (internal visibility for testing)
- `GetDirectorySeparator(ImapClient)` — static helper (internal visibility)

## Dependencies

- Uses: `ILogger`
- Used by: `EmailSyncService`
