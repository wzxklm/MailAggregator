# Mail Services — Discovery, Connection, Sync, Send, Account, SyncManager

## Auto-Discovery — `Services/Discovery/AutoDiscoveryService.cs`

5-level fallback to find IMAP/SMTP server config from email address:

- **Level 1**: `https://autoconfig.{domain}/mail/config-v1.1.xml`
- **Level 2**: `https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml`
- **Level 3**: `https://autoconfig.thunderbird.net/v1.1/{domain}` (Thunderbird ISPDB)
- **Level 4**: DNS MX query → retry Level 1-3 with MX domain
- **Level 5**: null (UI prompts manual config)
- **XML parsing**: `ParseAutoconfigXml()` from Thunderbird-format XML
- **MX parsing**: `nslookup` process, extract base domain; handles ccSLDs (co.uk, com.au, etc.) via `TwoLevelTlds` set
- **MX security**: `ValidDomainRegex` prevents command injection; linked `CancellationTokenSource` enforces 10s nslookup timeout with `process.Kill()` on expiry
- **Timeout**: 10s per HTTP request, 10s per nslookup
- **Interface**: `IAutoDiscoveryService` — `DiscoverAsync(emailAddress)`

---

## Connection Helper — `Services/Mail/MailConnectionHelper.cs`

Shared logic for all IMAP/SMTP connections (internal static).

- **Constants**: `MaxRetries = 3`, `InitialRetryDelay = 1s`, `TokenRefreshGracePeriod = 60s`
- `GetSecureSocketOptions(encryption)` — map `ConnectionEncryptionType` → MailKit `SecureSocketOptions`
- `ConfigureProxy(client, account, protocol, logger)` — SOCKS5 proxy setup
- `AuthenticateAsync(client, account, ...)` — unified auth:
  - OAuth2: check token expiry (60s grace) → refresh if needed → OAuth2 SASL
  - Password: decrypt → plaintext auth

## IMAP Connection — `Services/Mail/ImapConnectionService.cs`

- 3 retries + exponential backoff
- SOCKS5 proxy support
- OAuth token refresh + persist refreshed tokens (`PersistRefreshedTokenAsync`)
- **Interface**: `IImapConnectionService` — `ConnectAsync(account)`

## SMTP Connection — `Services/Mail/SmtpConnectionService.cs`

- Same pattern as IMAP (retry, proxy, OAuth refresh, persist)
- **Interface**: `ISmtpConnectionService` — `ConnectAsync(account)`

## Connection Pool — `Services/Mail/ImapConnectionPool.cs`

Reuse IMAP connections to avoid repeated TCP+TLS+AUTH handshakes.

- `ConcurrentDictionary<int, ConcurrentQueue<ImapClient>>` (per-account)
- **Max connections**: 2 per account
- **Validation**: check `IsConnected && IsAuthenticated` before reuse
- **Graceful degradation**: stale connections auto-released
- `GetConnectionAsync(account)` / `ReturnToPool(accountId, client)` / `RemoveAccount(accountId)`
- **Interface**: `IImapConnectionPool`

### `PooledImapConnection.cs`
- `Dispose()` returns connection to pool (not close)

---

## Email Sync — `Services/Mail/EmailSyncService.cs`

Uses `IDbContextFactory<MailAggregatorDbContext>` — each operation creates its own scoped DbContext for thread safety (background sync runs on separate threads from UI).

### Folder sync (`SyncFoldersCoreAsync`)
- Get IMAP folder list → identify SPECIAL-USE attributes → sync to DB (add/update/delete)

### Initial sync (`SyncInitialAsync`)
- Search last 30 days
- Fetch summaries (envelope, flags, BodyStructure, PreviewText)
- **No full body** (lazy load)
- Batch save every 50 messages

### Incremental sync (`SyncIncrementalAsync`)
- Check UIDVALIDITY (if changed → reset cache, re-sync)
- Fetch new messages (UID > MaxUid)
- Detect deleted messages (server UIDs vs local UIDs)
- Update folder counts

### Message operations
- `SetMessageReadAsync` — update `\Seen` flag
- `DownloadAttachmentAsync` — download to disk
- `MoveMessageAsync` — server-side move
- `DeleteMessageAsync` — move to Trash
- `FetchMessageBodyAsync` — lazy-fetch full body + attachment metadata

### Concurrency
- `SaveChangesSafeAsync` — handle EF Core concurrency conflicts (detach old entity, retry)
- **Interface**: `IEmailSyncService`

---

## Email Send — `Services/Mail/EmailSendService.cs`

- `SendAsync(account, to, cc, bcc, subject, body, isHtml, attachmentPaths)` — new email
- `ReplyAsync` / `ReplyAllAsync` — set In-Reply-To / References for threading
- `ForwardAsync` — fetch original attachments from IMAP and include
- **Sent folder**: After sending, `AppendToSentFolderAsync` saves a copy to the IMAP Sent folder via APPEND (most servers don't auto-save). Failures are logged but don't throw (email was already sent).
- **Quote format**: HTML uses `<blockquote>`, plaintext uses `> ` prefix
- **MIME**: Multipart MIME + Base64 attachment encoding
- **Interface**: `IEmailSendService`

---

## Account Management — `Services/AccountManagement/AccountService.cs`

- **AddAccountAsync(emailAddress, password?)** — flow:
  0. Check for duplicate email (+ unique DB index on `EmailAddress`) → 1. AutoDiscovery → 2. Create Account entity → 3. Check OAuth provider → 4. OAuth → set AuthType=OAuth2 / 5. Password → encrypt & store → 6. Validate IMAP (password only) → 7. Save to DB
- **UpdateAccountAsync** — update settings
- **DeleteAccountAsync** — full cleanup: 1. Stop SyncManager for account → 2. Release ImapConnectionPool → 3. Delete attachment files from disk → 4. Cascade delete DB entities (account + folders + messages + attachments)
- **GetAllAccountsAsync** / **GetAccountByIdAsync**
- **ValidateConnectionAsync** — test IMAP connection
- **Dependencies**: Injects `ISyncManager` and `IImapConnectionPool` for deletion cleanup
- **Interface**: `IAccountService`

---

## Background Sync — `Services/Sync/SyncManager.cs`

IMAP IDLE background sync orchestrator.

- **Constants**: `IdleTimeout = 29min` (RFC 2177 < 30min), `InitialReconnectDelay = 1s`, `MaxReconnectDelay = 60s`
- **Concurrency**: `ConcurrentDictionary<int, (Task, CancellationTokenSource)>` per-account
- **IDLE loop** (`AccountSyncLoopAsync`):
  1. Connect IMAP → 2. Sync folders → 3. Inbox incremental sync → 4. Open Inbox, enter IDLE
  5. IDLE cycle: wait 29min or new mail → detect new messages → incremental sync → fire `NewEmailsReceived` → re-enter IDLE
- **Exponential backoff**: `delay = min(1s × 2^attempt, 60s)`
- **Auth error handling**: non-transient auth errors → stop (no retry)
- **Graceful shutdown**: `StopAllAsync()` — cancel all tokens, await all tasks
- **Event**: `NewEmailsReceived` (`EventHandler<NewEmailsEventArgs>`)
- **Interface**: `ISyncManager`

---

## Concurrency & Thread Safety

| Component | Mechanism |
|-----------|-----------|
| IMAP pool | `ConcurrentDictionary` + `ConcurrentQueue` |
| SyncManager | `ConcurrentDictionary.GetOrAdd()` (atomic, no TOCTOU) |
| DB operations | `IDbContextFactory` → scoped DbContext per operation (EmailSyncService, ImapConnectionService, SmtpConnectionService) |
| EF Core save | `SaveChangesSafeAsync()` — detach + retry on conflict |
| Batch save | Every 50 messages (avoid memory bloat) |
| UI updates | `Dispatcher.Invoke()` for UI thread |
| Folder switch | `CancellationToken` cancels previous load |
