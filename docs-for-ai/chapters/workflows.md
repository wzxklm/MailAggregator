# Core Workflows

## 1. Account Creation Flow

```
User enters email address
    │
    ▼
AddAccountViewModel.DiscoverAsync()
    │
    ▼
AutoDiscoveryService.DiscoverAsync(emailAddress)
    ├─ Level 1: https://autoconfig.{domain}/mail/config-v1.1.xml
    ├─ Level 2: https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml
    ├─ Level 3: https://autoconfig.thunderbird.net/v1.1/{domain}
    ├─ Level 4: MX query → retry Level 1-3 with MX domain
    └─ Level 5: null (manual config)
    │
    ▼
OAuthService.FindProviderByHost(imapHost)
    ├─ Found → UI shows "OAuth 2.0" option
    └─ Not found → UI shows "Password" option
    │
    ▼ (user saves)
    │
    ├── OAuth path ─────────────────────────────┐
    │   PrepareAuthorization() → auth URL + PKCE │
    │   → Open browser                           │
    │   → WaitForAuthorizationCodeAsync(port)    │
    │   → ExchangeCodeForTokenAsync()            │
    │   → Encrypt & store tokens                 │
    │                                            │
    ├── Password path ──────────────────────────┐│
    │   PasswordAuthService.StorePassword()     ││
    │   → AES-256-GCM encrypt                   ││
    │   → ImapConnectionService.ConnectAsync()   ││
    │   → Validate IMAP connection              ││
    │                                           ││
    ▼                                           ▼▼
AccountService → DbContext.SaveChangesAsync()
    │
    ▼
MainViewModel.LoadAccountsAsync()
    → Sync folder list → Build folder tree → Start SyncManager
```

## 2. Email Sync Flow

```
SyncManager.StartAccountSyncAsync(account)
    │
    ▼
AccountSyncLoopAsync(account) — main loop
    │
    ├── Connect ────────────────────────────────────┐
    │   ImapConnectionPool.GetConnectionAsync()      │
    │   → Check pool for valid connection            │
    │   → If none: ImapConnectionService.ConnectAsync│
    │     → Configure SOCKS5 proxy (if any)          │
    │     → MailConnectionHelper.AuthenticateAsync()  │
    │       → OAuth: check expiry → refresh → SASL   │
    │       → Password: decrypt → plaintext auth     │
    │   → Return PooledImapConnection                │
    │                                                │
    ├── Folder sync ────────────────────────────────┤
    │   EmailSyncService.SyncFoldersAsync(account)   │
    │                                                │
    ├── Incremental sync ───────────────────────────┤
    │   EmailSyncService.SyncIncrementalAsync(Inbox) │
    │   → Check UIDVALIDITY → Fetch new → Detect     │
    │     deleted → Batch save (every 50)            │
    │                                                │
    ├── IDLE phase ─────────────────────────────────┤
    │   client.IdleAsync(29min timeout)              │
    │   ├─ New mail → SyncIncrementalAsync()         │
    │   │           → Fire NewEmailsReceived          │
    │   │           → UI: insert + Toast notification │
    │   └─ Timeout → Reopen Inbox → Continue IDLE    │
    │                                                │
    └── Error handling ─────────────────────────────┤
        → Auth error (non-transient) → Stop          │
        → Connection error → Exponential backoff     │
          delay = min(1s × 2^attempt, 60s)           │
        → Successful reconnect → Reset backoff       │
```

## 3. Email Viewing Flow

```
User clicks folder in tree
    │
    ▼
MainViewModel.SelectFolderAsync(node)
    ├─ Cancel previous load (CancellationToken)
    ├─ EmailSyncService.SyncIncrementalAsync(folder)
    ├─ Load ≤200 emails from DB (date desc, projection query — no body)
    └─ Update Emails collection
    │
    ▼
User clicks email in list
    │
    ▼
LoadFullMessageAndMarkReadAsync()
    ├─ Query full email from DB (BodyHtml/BodyText + Attachments)
    ├─ If not cached → FetchMessageBodyAsync() from IMAP → cache to DB
    ├─ If unread → SetMessageReadAsync() → IMAP \Seen flag
    └─ Trigger PropertyChanged → MainWindow updates preview
    │
    ▼
MainWindow.UpdateEmailPreview()
    ├─ Has BodyHtml → WebView2.NavigateToString(html)
    └─ Text only → WebView2.NavigateToString("<pre>" + text + "</pre>")
```

## 4. Email Sending Flow

```
User clicks "New" / "Reply" / "Forward"
    │
    ▼
MainViewModel → Create ComposeViewModel
    ├─ New: blank form
    ├─ Reply: fill To, Subject("Re: ..."), quote body
    ├─ ReplyAll: fill To+Cc, Subject, quote body
    └─ Forward: fill Subject("Fwd: ..."), forward body
    │
    ▼
ComposeViewModel.SendAsync()
    ├─ Validate: SelectedSender ≠ null, To not empty
    ├── New → EmailSendService.SendAsync()
    │   → Build MimeMessage → SMTP connect → send → disconnect
    ├── Reply/ReplyAll → EmailSendService.ReplyAsync()
    │   → Set In-Reply-To/References → quote body → SMTP send
    └── Forward → EmailSendService.ForwardAsync()
        → Fetch original from IMAP (with attachments) → SMTP send
```

## 5. IMAP Connection Lifecycle

```
Request connection
    │
    ▼
ImapConnectionPool.GetConnectionAsync(account)
    ├─ Dequeue from pool → validate IsConnected && IsAuthenticated
    │  → Valid → return PooledImapConnection
    │  → Invalid → release, try next
    └─ Pool empty → create new via ImapConnectionService.ConnectAsync()
       → Retry loop (max 3, exponential backoff)
       → ConfigureProxy() → ConnectAsync() → AuthenticateAsync()
       → PersistRefreshedTokenAsync() (if OAuth refreshed)
    │
    ▼
Return PooledImapConnection
    │
    ▼ (after use, Dispose)
    │
    ▼
PooledImapConnection.Dispose()
    → ReturnToPool(accountId, client)
      → Valid & pool not full → enqueue
      → Otherwise → close & release
```
