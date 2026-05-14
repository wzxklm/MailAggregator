# Mail — IMAP/SMTP connection, sync, and send

## Files

| File | Chapter | Responsibility |
|------|---------|----------------|
| `src/MailAggregator.Core/Services/Mail/IImapConnectionService.cs` | [imap-connection.md](imap-connection.md) | IMAP connection interface |
| `src/MailAggregator.Core/Services/Mail/ImapConnectionService.cs` | [imap-connection.md](imap-connection.md) | Connect + authenticate IMAP with retry, XOAUTH2/PLAIN, proxy |
| `src/MailAggregator.Core/Services/Mail/ISmtpConnectionService.cs` | [smtp-connection.md](smtp-connection.md) | SMTP connection interface |
| `src/MailAggregator.Core/Services/Mail/SmtpConnectionService.cs` | [smtp-connection.md](smtp-connection.md) | Connect + authenticate SMTP with retry, XOAUTH2/PLAIN, proxy |
| `src/MailAggregator.Core/Services/Mail/IImapConnectionPool.cs` | [connection-pool.md](connection-pool.md) | Connection pool interface |
| `src/MailAggregator.Core/Services/Mail/ImapConnectionPool.cs` | [connection-pool.md](connection-pool.md) | Per-account pooling of IMAP connections with cleanup timer |
| `src/MailAggregator.Core/Services/Mail/PooledImapConnection.cs` | [connection-pool.md](connection-pool.md) | Disposable wrapper that returns connection to pool |
| `src/MailAggregator.Core/Services/Mail/IEmailSyncService.cs` | [email-sync.md](email-sync.md) | Sync service interface (folder sync, initial/incremental message sync) |
| `src/MailAggregator.Core/Services/Mail/EmailSyncService.cs` | [email-sync.md](email-sync.md) | Folder sync, initial/incremental message sync, flag sync, deletion detection |
| `src/MailAggregator.Core/Services/Mail/IEmailOperationService.cs` | [email-operations.md](email-operations.md) | Message operation interface (read, delete, move, download, fetch body) |
| `src/MailAggregator.Core/Services/Mail/EmailOperationService.cs` | [email-operations.md](email-operations.md) | Single-message IMAP operations: read flags, delete, move, download attachment, fetch body |
| `src/MailAggregator.Core/Services/Mail/ImapFolderDiscovery.cs` | [folder-discovery.md](folder-discovery.md) | Thunderbird-style 4-strategy IMAP folder discovery + reflection-based FolderCache injection |
| `src/MailAggregator.Core/Services/Mail/IEmailSendService.cs` | [email-send.md](email-send.md) | Send service interface |
| `src/MailAggregator.Core/Services/Mail/EmailSendService.cs` | [email-send.md](email-send.md) | Send, reply, reply-all, forward with Sent-folder append |
| `src/MailAggregator.Core/Services/Mail/MailConnectionHelper.cs` | [connection-helper.md](connection-helper.md) | Shared auth, proxy, retry, encryption-mapping logic |

## Overview

The Mail domain handles all IMAP and SMTP interactions: connecting/authenticating to mail servers, pooling IMAP connections for reuse, syncing folder trees and messages from IMAP, performing single-message operations (read/delete/move/download/fetch body), and composing/sending emails via SMTP. Sync logic lives in `EmailSyncService`, single-message operations in `EmailOperationService`, and folder discovery strategies in `ImapFolderDiscovery`. All connection services delegate shared logic (auth, proxy, encryption mapping, token refresh) to `MailConnectionHelper`. `ImapConnectionPool` sits between connection creation and sync/send/operation consumers, avoiding repeated TCP+TLS+AUTH handshakes.
