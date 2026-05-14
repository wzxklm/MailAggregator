# EmailOperationService — Single-message IMAP operations

## Overview

Handles individual message operations against IMAP servers: setting read/unread flags, downloading attachments, moving messages between folders, deleting messages (move to Trash or expunge), and fetching full message bodies with inline image resolution. Extracted from `EmailSyncService` to separate single-message operations from bulk sync logic.

## Key Behaviors

- **Set read status**: `SetMessageReadAsync` opens folder ReadWrite, adds/removes `\Seen` flag, updates local DB via attach + mark-modified pattern.
- **Download attachment**: `DownloadAttachmentAsync` fetches full MIME message, finds matching `MimePart` by filename or ContentId, decodes content to disk, saves `LocalPath` to DB.
- **Move message**: `MoveMessageAsync` uses IMAP `MoveToAsync`, updates local `FolderId` and `Uid` (if server returns new UID).
- **Delete message**: `DeleteMessageAsync` finds Trash folder by `SpecialFolderType.Trash`. If already in Trash → add `\Deleted` flag + `Expunge` + remove from DB. Otherwise → `MoveMessageAsync` to Trash.
- **Fetch body**: `FetchMessageBodyAsync` fetches full MIME message, extracts `BodyHtml`/`BodyText`, resolves `cid:` references to `data:` URIs via `ResolveInlineImages`, caches attachment metadata. Uses attach + mark-modified pattern for DB update.
- **Inline image resolution**: `ResolveInlineImages` (internal static) replaces `cid:` references in HTML with base64 `data:` URIs so WebView2 renders embedded images without external resource resolution.
- **Pooled connection retry**: `WithPooledConnectionAsync` retries once on `IOException` (dead pooled TCP connection).

## Interface

`IEmailOperationService`
- `SetMessageReadAsync(Account, EmailMessage, bool, CancellationToken)` — set/clear `\Seen` flag
- `DownloadAttachmentAsync(Account, EmailMessage, EmailAttachment, string savePath, CancellationToken)`
- `MoveMessageAsync(Account, EmailMessage, MailFolder dest, CancellationToken)`
- `DeleteMessageAsync(Account, EmailMessage, CancellationToken)` — move to Trash, or expunge if already in Trash
- `FetchMessageBodyAsync(Account, EmailMessage, CancellationToken)` — lazy body + attachment metadata fetch

## Dependencies

- Uses: `IImapConnectionPool`, `IDbContextFactory<MailAggregatorDbContext>`
- Used by: `MainViewModel`
