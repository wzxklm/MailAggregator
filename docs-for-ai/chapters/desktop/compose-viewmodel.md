# ComposeViewModel — Email compose, reply, forward

## Overview

Handles composing new emails and replying to / forwarding existing messages. Supports multiple sender accounts, To/Cc/Bcc fields, file attachments, and four compose modes. Launched from `MainViewModel` as a non-modal window.

## Key Behaviors

- **Compose modes**: `ComposeMode` enum: `New`, `Reply`, `ReplyAll`, `Forward`. Mode determines which `IEmailSendService` method is called
- **Reply/Forward preparation**: `PrepareReply()` pre-fills To, Cc, Subject (with `Re:`/`Fwd:` prefix deduplication), and quoted body text
- **Quoted body**: Plain-text format with "--- Original Message ---" header, includes sender, date, subject, and original body text
- **Sender selection**: `SetSenderAccounts()` populates sender dropdown; defaults to first account or the account that received the original message
- **Attachments**: `OpenFileDialog` with multi-select; paths stored in `AttachmentPaths` collection. Passed as `IReadOnlyList<string>` to send service
- **Validation**: Requires selected sender and non-empty To field before sending
- **Send dispatch**: Routes to `SendAsync`, `ReplyAsync`, `ReplyAllAsync`, or `ForwardAsync` based on `Mode`. Fires `CloseRequested` on success

## Interface

`ComposeViewModel` (no interface)

Commands: `SendCommand`, `AddAttachmentCommand`, `RemoveAttachmentCommand`

Properties: `SenderAccounts`, `SelectedSender`, `To`, `Cc`, `Bcc`, `Subject`, `Body`, `AttachmentPaths`, `IsSending`, `StatusText`, `Mode`, `WindowTitle`

Events: `CloseRequested` (fired after successful send)

## Dependencies

- Uses: `IEmailSendService`, `ILogger`
- Used by: `MainViewModel` (opens dialog via `ComposeNew`, `Reply`, `ReplyAll`, `Forward` commands)

---

## ComposeWindow (view)

Code-behind wires `vm.CloseRequested` to `Close()` on `Loaded`. Non-modal window (uses `Show()`, not `ShowDialog()`).
