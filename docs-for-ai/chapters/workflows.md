# Core Workflows

## 1. Account Setup (Password Auth)

```
User clicks "Add Account"
├── AccountListViewModel → opens AddAccountWindow
│
├── Step 0: Email Entry
│   └── User enters email address
│
├── Step 1: Auto-Discovery
│   ├── AddAccountViewModel.DiscoverCommand()
│   │   └── AutoDiscoveryService.DiscoverAsync(email)
│   │       ├── Try autoconfig.{domain}/mail/config-v1.1.xml (HTTPS+HTTP parallel)
│   │       ├── Try {domain}/.well-known/autoconfig/mail/config-v1.1.xml
│   │       ├── Try Thunderbird ISPDB (autoconfig.thunderbird.net)
│   │       ├── Resolve MX record → retry levels 1-3 with MX domain
│   │       ├── Try RFC 6186 SRV records (_imaps._tcp, _submission._tcp)
│   │       └── Return null → prompt manual config
│   │
│   ├── On success → populate IMAP/SMTP fields → Step 2
│   └── On skip/failure → Step 3 (manual server config)
│
├── Step 2: Authentication
│   ├── Password selected
│   │   └── User enters password → stored in ViewModel memory
│   └── → Step 3 or Step 4
│
├── Step 3: Server Configuration (manual or edit discovered)
│   └── User confirms/edits IMAP host/port/encryption, SMTP host/port/encryption
│       └── Optional: proxy host:port
│
├── Step 4: Save
│   └── AccountService.AddAccountAsync()
│       ├── Duplicate email check (DB query)
│       ├── PasswordAuthService.StorePassword()
│       │   └── CredentialEncryptionService.Encrypt(password) → Account.EncryptedPassword
│       ├── ImapConnectionService.ConnectAsync() → validate IMAP connectivity
│       │   └── On failure → throw, account not saved
│       ├── Transaction: save Account to DB
│       └── Return Account
│
└── Post-Save
    └── SyncManager.StartAccountSyncAsync(account)
        └── → Workflow #3 (Initial Sync)
```

## 2. Account Setup (OAuth Auth)

```
(Steps 0-1 same as Workflow #1)
│
├── Step 2: OAuth Flow
│   ├── OAuthService.FindProviderByHost(imapHost) → OAuthProviderConfig
│   ├── OAuthService.PrepareAuthorization(provider, loginHint)
│   │   ├── Generate PKCE code_verifier + S256 challenge
│   │   ├── Generate CSRF state parameter
│   │   ├── Pre-start HttpListener on ephemeral port (retry up to 10×)
│   │   └── Return (authUrl, codeVerifier, port, redirectUri)
│   │
│   ├── Open browser → authUrl
│   │
│   ├── OAuthService.WaitForAuthorizationCodeAsync(port, 3-min timeout)
│   │   ├── Receive callback on HttpListener
│   │   ├── Validate CSRF state parameter
│   │   ├── Extract authorization code
│   │   └── Send HTML success page to browser
│   │
│   ├── OAuthService.ExchangeCodeForTokenAsync(provider, code, verifier, redirectUri)
│   │   ├── POST to token endpoint with code + PKCE verifier
│   │   ├── CredentialEncryptionService.Encrypt(accessToken)
│   │   ├── CredentialEncryptionService.Encrypt(refreshToken)
│   │   └── Return OAuthTokenResult (encrypted tokens, expiry, scopes)
│   │
│   └── Store tokens on Account entity
│
├── Step 3-4: Same as Workflow #1 (server config → save)
│
└── Post-Save → SyncManager starts
```

## 3. Initial Email Sync

```
SyncManager.StartAccountSyncAsync(account)
├── ImapConnectionService.ConnectAsync(account)
│   ├── Resolve host + configure SSL/STARTTLS/proxy
│   ├── Authenticate (PLAIN or XOAUTH2)
│   │   ├── Password: decrypt → PLAIN auth
│   │   └── OAuth: check expiry → refresh if needed → XOAUTH2 auth
│   └── Retry up to 3× with exponential backoff (1s, 2s, 4s)
│
├── First Connection (no folders in DB)
│   └── EmailSyncService.SyncFoldersAsync(account, client)
│       ├── 4-tier folder discovery:
│       │   ├── (1) PersonalNamespaces[0].GetSubfolders()
│       │   ├── (2) Default namespace + reflection-inject root into FolderCache
│       │   ├── (3) Root folder GetSubfolders(subscribedOnly: false)
│       │   └── (4) INBOX-only fallback
│       ├── Map SPECIAL-USE attributes → SpecialFolderType
│       ├── Always include INBOX
│       ├── Create/update MailFolder entities in DB
│       └── Raise FoldersSynced event → UI refreshes folder tree
│
├── EmailSyncService.SyncInitialAsync(account, inboxFolder)
│   ├── Acquire per-folder SemaphoreSlim lock
│   ├── SEARCH for messages from last 30 days
│   ├── FETCH envelopes + preview text in batches of 50
│   ├── Create EmailMessage entities (headers only, no body)
│   └── SaveChangesAsync → DbContext stamps CachedAt
│
└── Enter watch loop → Workflow #4 or #5
```

## 4. Continuous Sync (IDLE Mode)

```
Watch Loop (IDLE enabled, Account.UseIdle=true)
│
├── Open INBOX folder (ReadWrite)
├── STATUS command → refresh message count, reset server idle timer
│
├── client.IdleAsync(29-minute timeout, cancellationToken)
│   ├── Server pushes EXISTS (new message) → CountChanged event fires
│   │   └── Cancel IDLE immediately
│   ├── 29-minute timeout reached (RFC 2177 limit)
│   │   └── Exit IDLE, loop back to STATUS
│   └── IDLE rejected by server
│       ├── Increment failure counter
│       ├── After 2 failures → switch to polling (Workflow #5)
│       └── Otherwise retry IDLE
│
├── On CountChanged or timeout:
│   ├── STATUS → get current message count
│   ├── If count > previous:
│   │   ├── EmailSyncService.SyncIncrementalAsync()
│   │   │   ├── Acquire per-folder lock
│   │   │   ├── Check UIDVALIDITY
│   │   │   │   ├── Changed → wipe folder → re-sync initial
│   │   │   │   └── Same → continue
│   │   │   ├── SEARCH UID > maxStoredUid
│   │   │   ├── FETCH new message envelopes
│   │   │   ├── Detect deleted UIDs → remove from DB
│   │   │   ├── Sync flags (IsRead, IsFlagged) in single roundtrip
│   │   │   └── Save changes
│   │   └── Raise NewEmailsReceived event → UI updates
│   └── If count unchanged → loop back to IDLE
│
└── On error:
    ├── OAuthReauthenticationRequiredException → stop sync, user must re-auth
    ├── Non-transient auth error → stop sync
    └── Transient error → Workflow #6 (Reconnection)
```

## 5. Continuous Sync (Polling Fallback)

```
Watch Loop (IDLE disabled or failed 2×)
│
├── Task.Delay(59 seconds)
├── STATUS INBOX → refresh message count
├── If count > previous:
│   └── EmailSyncService.SyncIncrementalAsync() → same as Workflow #4
│       └── Raise NewEmailsReceived event
└── Loop back to delay
```

## 6. Network-Aware Reconnection

```
Sync loop error (connection lost, timeout, etc.)
│
├── Check NetworkInterface.GetIsNetworkAvailable()
│   ├── Network DOWN:
│   │   ├── Wait on ManualResetEventSlim (blocks thread)
│   │   ├── NetworkChange.NetworkAvailabilityChanged fires
│   │   │   └── Signal ManualResetEvent → wake up
│   │   └── Reset backoff counter to 0
│   │
│   └── Network UP:
│       ├── Calculate delay = min(1s × 2^attempt, 300s)
│       ├── Apply ±25% jitter (Random.Shared)
│       └── Task.Delay(jitteredDelay)
│
└── Reconnect → ImapConnectionService.ConnectAsync()
    ├── Success → resume watch loop (Workflow #4 or #5)
    └── Failure → increment attempt, loop back to check
```

## 7. Email Browsing & Body Fetch

```
User clicks folder in tree
├── MainViewModel.SelectFolderCommand(node)
│   ├── Cancel any in-flight folder load (CancellationTokenSource)
│   ├── EmailSyncService.SyncIncrementalAsync(account, folder)
│   └── Load messages from DB → display in email list
│
User selects email in list
├── MainViewModel.LoadFullMessageAndMarkReadAsync()
│   ├── If body not cached (BodyHtml/BodyText null) OR contains unresolved cid:
│   │   └── EmailSyncService.FetchMessageBodyAsync(account, message)
│   │       ├── Get pooled IMAP connection (retry once on IOException)
│   │       ├── FETCH full message by UID
│   │       ├── Extract BodyHtml, BodyText, PreviewText
│   │       ├── Resolve cid: references → base64 data URIs
│   │       ├── Extract attachment metadata (filename, size, contentId)
│   │       └── Save to DB
│   │
│   ├── Mark as read (if unread):
│   │   ├── EmailSyncService.SetMessageReadAsync()
│   │   │   └── IMAP: AddFlagsAsync(\Seen) → update DB IsRead=true
│   │   └── Update folder unread count in UI
│   │
│   └── Render in WebView2
│       ├── HTML content → NavigateToString()
│       ├── External images blocked by default (anti-tracking)
│       ├── User clicks "Load Images" → re-render with images enabled
│       └── JavaScript disabled (XSS prevention)
```

## 8. Email Compose & Send

```
User clicks New / Reply / ReplyAll / Forward
├── MainViewModel opens ComposeWindow
│   ├── ComposeViewModel.SetSenderAccounts(accounts)
│   └── ComposeViewModel.PrepareReply(originalMsg, account, mode)
│       ├── Reply: To=originalSender, Subject="Re: ...", quote body
│       ├── ReplyAll: To=sender+allTo (excl self), Cc=allCc (excl self)
│       ├── Forward: To=empty, Subject="Fwd: ...", fetch original attachments
│       └── Quote format: HTML=<blockquote>, Plain="> " prefix
│
User fills To/Cc/Bcc, Subject, Body, Attachments
├── ComposeViewModel.SendCommand()
│   ├── Validate email addresses (basic @-check)
│   │
│   ├── EmailSendService.{Send|Reply|ReplyAll|Forward}Async()
│   │   ├── Build MimeMessage (multipart/mixed if attachments)
│   │   │   ├── Set From, To, Cc, Bcc, Subject
│   │   │   ├── Reply: set In-Reply-To + References headers
│   │   │   ├── Forward: fetch + attach original attachments from IMAP
│   │   │   └── Attach local files
│   │   │
│   │   ├── SmtpConnectionService.ConnectAsync(account)
│   │   │   └── Auth (PLAIN/XOAUTH2) + send via SMTP
│   │   │
│   │   └── Append to Sent folder via IMAP
│   │       ├── ImapConnectionService.ConnectAsync(account)
│   │       ├── Find Sent folder (SpecialFolderType.Sent)
│   │       ├── Append with \Seen flag
│   │       └── On failure → log warning, continue (non-fatal)
│   │
│   └── Close compose window on success
```

## 9. Account Deletion

```
User selects account → clicks Delete → confirms MessageBox
├── AccountService.DeleteAccountAsync(accountId)
│   ├── SyncManager.StopAccountSyncAsync(accountId)
│   │   └── Cancel CancellationTokenSource → await sync task completion
│   │
│   ├── ImapConnectionPool.RemoveAccount(accountId)
│   │   └── Disconnect + dispose all pooled ImapClients
│   │
│   ├── MailConnectionHelper.RemoveTokenRefreshLock(accountId)
│   │   └── Remove SemaphoreSlim from ConcurrentDictionary
│   │
│   ├── Query attachment file paths (join Messages → Attachments)
│   ├── Delete attachment files from disk
│   │   └── Per-file try/catch (log warning on failure, orphaned files possible)
│   │
│   ├── Detach stale tracked entity (ChangeTracker)
│   ├── Re-fetch fresh Account entity
│   └── DbContext.Remove(account) → SaveChangesAsync()
│       └── CASCADE: Account → Folders → Messages → Attachments (DB rows)
│
└── MainViewModel refreshes account list + folder tree
```

## 10. 2FA TOTP Management

```
User opens 2FA window
├── TwoFactorViewModel.InitializeAsync()
│   ├── TwoFactorAccountService.GetAllAsync() → list accounts
│   ├── Per account: GetDecryptedSecret() → decrypt via CredentialEncryptionService
│   ├── Wrap each in TwoFactorDisplayItem(account, secret, codeService)
│   └── Start DispatcherTimer (1-second interval)
│
Timer tick (every 1 second)
├── Per TwoFactorDisplayItem.UpdateCode()
│   ├── TwoFactorCodeService.GetRemainingSeconds(period) → countdown
│   ├── If new period started:
│   │   └── TwoFactorCodeService.GenerateCode(secret, algorithm, digits, period)
│   │       ├── Decode base32 → bytes
│   │       ├── Compute TOTP via OtpNet
│   │       ├── CryptographicOperations.ZeroMemory(secretBytes)
│   │       └── Return code (e.g., "123 456")
│   └── Update RemainingSeconds property → progress bar
│
User clicks "Add"
├── Opens AddTwoFactorWindow
│   ├── Manual: enter issuer, label, base32 secret, algorithm, digits, period
│   └── URI: paste otpauth://totp/... → ParseOtpAuthUri() extracts all params
├── TwoFactorAccountService.AddAsync()
│   ├── Validate + normalize secret to uppercase
│   ├── Validate secret generates valid code
│   ├── CredentialEncryptionService.Encrypt(secret) → EncryptedSecret
│   └── Save TwoFactorAccount to DB
└── Refresh display list

User clicks "Copy Code"
└── Strip spaces → Clipboard.SetText(code)

User clicks "Delete" → confirm → TwoFactorAccountService.DeleteAsync() → permanent
```
