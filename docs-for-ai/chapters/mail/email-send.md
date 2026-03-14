# EmailSendService — Compose, send, reply, and forward emails

## Overview

Handles all write-side email operations: composing new messages, replying (single/all), and forwarding with original attachments. After sending via SMTP, appends a copy to the IMAP Sent folder. Uses `ISmtpConnectionService` for sending and `IImapConnectionService` for Sent-folder append and fetching original messages during forward.

## Key Behaviors

- **Send flow**: Build `MimeMessage` → SMTP send → disconnect → append to Sent folder via IMAP. Sent-folder append failure is logged as warning, not thrown (email was already sent).
- **Reply**: Sets `In-Reply-To` and `References` headers. Prefixes subject with `Re:` (idempotent). Quotes original body in `<blockquote>` (HTML) or `> ` prefixed lines (plain text).
- **Reply All**: Same as Reply but adds all original To/Cc recipients, excluding the sender's own address.
- **Forward**: Prefixes subject with `Fwd:` (idempotent). Fetches original MIME message from IMAP to get original attachments. Combines user attachments + original attachments in `multipart/mixed`.
- **Address validation**: `ParseAndValidateAddresses` uses `InternetAddressList.TryParse` then validates each `MailboxAddress` has `@` with text on both sides. Throws `ArgumentException` with details on invalid addresses.
- **Attachment handling**: `BuildMessageBody` opens file streams for local attachments, sets `ContentDisposition.Attachment` + `Base64` encoding. Returns `TextPart` when no attachments, `Multipart("mixed")` otherwise. Disposes opened streams on error.

## Interface

`IEmailSendService`
- `SendAsync(Account, to, cc?, bcc?, subject, body, isHtml, attachmentPaths?, CancellationToken)`
- `ReplyAsync(Account, EmailMessage original, body, isHtml, attachmentPaths?, CancellationToken)`
- `ReplyAllAsync(Account, EmailMessage original, body, isHtml, attachmentPaths?, CancellationToken)`
- `ForwardAsync(Account, EmailMessage original, to, cc?, bcc?, body, isHtml, additionalAttachmentPaths?, CancellationToken)`

## Internal Details

`SendMessageAsync` is the shared send path for all four operations:
1. `_smtpConnection.ConnectAsync` → `client.SendAsync` → `client.DisconnectAsync`
2. `AppendToSentFolderAsync` → finds Sent folder by `SpecialFolderType.Sent` in DB → `_imapConnection.ConnectAsync` → `folder.AppendAsync(message, MessageFlags.Seen)` → disconnect

`FetchOriginalMessageAsync` (used by Forward): looks up folder from DB, connects IMAP, fetches full `MimeMessage` by UID. Returns `null` on any error (forward proceeds without original attachments).

`BuildQuotedReply` / `BuildForwardBody`: static methods producing HTML (with `<blockquote>`) or plain text (with `> ` prefix / header block).

`GetSenderAddress`: uses `Account.DisplayName` if set, falls back to `EmailAddress`.

## Dependencies

- Uses: `ISmtpConnectionService`, `IImapConnectionService`, `MailAggregatorDbContext`
- Used by: `ComposeViewModel`
