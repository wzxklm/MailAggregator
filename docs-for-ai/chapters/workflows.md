# Core Workflows

## 1. Account Creation

```
Email → AddAccountViewModel.DiscoverAsync()
    → AutoDiscoveryService (L1-L6 fallback)
    → UpdateOAuthAvailability(imapHost) [also on manual host change]
    → User saves (SelectedAuthType passed explicitly)
    │
    ├── OAuth: PrepareAuthorization → browser → WaitForCode → Exchange → encrypt tokens
    └── Password: StorePassword → encrypt → ValidateIMAP
    │
    → AccountService.SaveChangesAsync() (transaction)
    → MainViewModel.LoadAccountsAsync() → sync folders → build tree → start SyncManager
```

## 2. Email Sync

```
SyncManager.StartAccountSyncAsync(account)
    → AccountSyncLoopAsync:
    │
    ├── Connect: Pool.GetConnectionAsync() → validate or create new
    │   → proxy → AuthenticateAsync (OAuth/Password)
    │
    ├── SyncFoldersAsync(account)
    │
    ├── SyncIncrementalAsync(Inbox) → UIDVALIDITY → new msgs → deletions → batch save
    │
    ├── IDLE (29min) or Poll (2min + NoOp)
    │   ├─ New mail → SyncIncremental → NewEmailsReceived → Toast
    │   └─ Timeout → Reopen → Continue
    │
    └── Errors: auth (non-transient) → stop | connection → backoff min(1s×2^n, 300s)
```

## 3. Email Viewing

```
Folder click → SelectFolderAsync: cancel prev → SyncIncremental → load ≤200 (no body)
    │
Email click → LoadFullMessageAndMarkReadAsync:
    DB body (cached?) → else IMAP fetch → cache → mark read → PropertyChanged
    │
UpdateEmailPreview: BodyHtml → WebView2 | text → <pre> wrapper
```

## 4. Email Sending

```
New/Reply/Forward → ComposeViewModel
    ├─ New: blank | Reply: To+quote | ReplyAll: To+Cc+quote | Forward: body+attachments
    │
SendAsync → validate → EmailSendService.Send/Reply/Forward
    → MimeMessage → SMTP → AppendToSentFolder
```

## 5. IMAP Connection Lifecycle

```
Pool.GetConnectionAsync(account)
    ├─ Dequeue → validate (IsConnected && IsAuthenticated) → return or release
    └─ Empty → ConnectAsync (3 retries) → proxy → auth → persist token
    │
Use → PooledImapConnection.Dispose() → ReturnToPool (if valid + not full, else close)
```

## 6. 2FA Account Management

```
"2FA" button → TwoFactorWindow → InitializeAsync:
    LoadAccountsAsync (GetAllAsync + decrypt secrets) + start 1s timer
    │
    ├── Add: AddTwoFactorWindow → Manual (fields) or URI (parse) → AddAsync/AddFromUriAsync → reload
    ├── Edit: AddTwoFactorWindow + LoadForEdit → UpdateAsync (issuer/label only) → reload
    └── Delete: confirm → DeleteAsync → remove from list
```

## 7. 2FA Code Display

```
LoadAccountsAsync → GetAllAsync → decrypt each → TwoFactorDisplayItem(account, secret, codeService)
    │
Timer 1s → UpdateCode() per item:
    ├─ GetRemainingSeconds(period)
    ├─ Regenerate if period boundary crossed or first call
    ├─ GenerateCode → Base32 decode → Totp → compute → ZeroMemory
    ├─ Format: 6-digit "123 456", 8-digit "1234 5678"
    └─ Update RemainingSeconds + ProgressPercentage
    │
CopyCodeCommand → strip spaces → Clipboard.SetText
```
