# Mail Services — Discovery, Connection, Sync, Send, Account, SyncManager

## Auto-Discovery — `AutoDiscoveryService.cs`

6-level fallback for IMAP/SMTP config:

- **L1**: `https://autoconfig.{domain}/mail/config-v1.1.xml` (+ HTTP)
- **L2**: `https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml` (+ HTTP)
- **L3**: `https://autoconfig.thunderbird.net/v1.1/{domain}`
- **L4**: DNS MX → retry L1-3 with MX domain
- **L5**: SRV records (`_imaps._tcp`, `_imap._tcp`, `_submission._tcp`) — SMTP SRV parallel with IMAP
- **L6**: null (manual config)
- **Parallel**: L1-3 (incl HTTP fallbacks) via `Task.WhenAny`. First success cancels rest via `CancellationTokenSource`
- **XML parsing**: `ParseAutoconfigXml()` — Thunderbird format. Extracts `<authentication>` into `ServerConfiguration.Authentication`
- **DNS**: `ILookupClient` (DnsClient.NET, injectable). `ExtractBaseDomain()` handles ccSLDs via `TwoLevelTlds`
- **Timeout**: 10s HTTP, 10s DNS
- **Interface**: `IAutoDiscoveryService` — `DiscoverAsync(emailAddress)`

---

## Connection Helper — `MailConnectionHelper.cs`

Internal static, shared by IMAP/SMTP.

- **Constants**: `MaxRetries=3`, `InitialRetryDelay=1s`, `TokenRefreshGracePeriod=60s`
- `GetSecureSocketOptions(encryption)` — enum → MailKit `SecureSocketOptions`
- `ConfigureProxy(client, account, protocol, logger)` — SOCKS5
- `AuthenticateAsync(client, account, ...)` — OAuth2 (expiry check → refresh → SASL) or Password (decrypt → auth)
- **Token refresh lock**: Per-account `SemaphoreSlim` in `ConcurrentDictionary` + double-check pattern. `RemoveTokenRefreshLock(accountId)` on account delete
- `IsNonTransientAuthError(ImapCommandException)` — checks for "Unsafe Login", "Authentication", "LOGIN", "not connected". Used by SyncManager, EmailSyncService, ImapConnectionService to stop immediately

## IMAP Connection — `ImapConnectionService.cs`

- 3 retries + exponential backoff, SOCKS5 proxy
- **IMAP ID (RFC 2971)**: `IdentifyAsync` before auth if `ImapCapabilities.Id` present. Non-fatal on failure
- **Non-transient auth bypass**: Rethrows `IsNonTransientAuthError` immediately (no retry)
- OAuth refresh + `PersistRefreshedTokenAsync`
- **Interface**: `IImapConnectionService` — `ConnectAsync(account)`

## SMTP Connection — `SmtpConnectionService.cs`

Same pattern as IMAP. **Interface**: `ISmtpConnectionService` — `ConnectAsync(account)`

## Connection Pool — `ImapConnectionPool.cs`

- `ConcurrentDictionary<int, ConcurrentQueue<ImapClient>>` per-account. Max 2 per account
- **Atomic size tracking**: `_poolCounts` via `AddOrUpdate`
- **Validation**: `IsConnected && IsAuthenticated` before reuse; stale auto-released
- **Cleanup timer**: 5min `Timer`, dequeue/re-enqueue live, dispose dead. Guards `_disposed`
- `GetConnectionAsync` / `ReturnToPool` / `RemoveAccount`
- **Interface**: `IImapConnectionPool`

### `PooledImapConnection.cs`
`Dispose()` returns to pool (not close)

---

## Email Sync — `EmailSyncService.cs`

Uses `IDbContextFactory` — scoped DbContext per operation (thread safety).

### Folder sync (`SyncFoldersCoreAsync`)
IMAP folder list → SPECIAL-USE mapping → DB sync (add/update/delete)

### Initial sync (`SyncInitialAsync`)
Last 30 days, summaries only (no body), batch save every 50

### Incremental sync (`SyncIncrementalAsync`) → `Task<int>`
- Returns count of new messages synced
- **IOException retry**: Retries once on `IOException` — pooled connections may appear alive (`IsConnected=true`) but have a dead TCP socket (silently dropped by firewall/NAT). First failure disposes the bad connection; retry gets a fresh one
- Check UIDVALIDITY (changed → reset + re-sync via `SyncInitialCoreAsync`)
- Fetch new (UID > MaxUid) + `SyncFlagsAndDetectDeletionsAsync`
- Folder update via `Attach` + `IsModified` (avoids `Update()` cascade)

### Flag sync & deletion (`SyncFlagsAndDetectDeletionsAsync`)
- Single FETCH (UIDs + Flags) — server returns only existing UIDs
- Updates `IsRead`/`IsFlagged` when different. Tracking-safe: builds dict from `ChangeTracker.Entries<EmailMessage>()` first
- Missing UIDs → batch `ExecuteDeleteAsync`

### Message operations
- `SetMessageReadAsync` / `DownloadAttachmentAsync` / `MoveMessageAsync` / `DeleteMessageAsync` (→ Trash)
- `FetchMessageBodyAsync` — lazy-fetch body + attachments
- `ResolveInlineImages` (private) — replaces `cid:` refs with `data:` URIs from MIME `BodyParts`

### Concurrency
- **Per-folder lock**: `_folderSyncLocks` (`ConcurrentDictionary<int, SemaphoreSlim>`). Public methods lock; `*CoreAsync` lock-free. UIDVALIDITY calls `SyncInitialCoreAsync` directly (avoids deadlock, reuses ImapClient)
- **`SaveChangesSafeAsync`**: Handles `DbUpdateConcurrencyException` (detach+retry) and SQLite error 19 (UNIQUE, detach Added+retry)
- **Interface**: `IEmailSyncService`

---

## Email Send — `EmailSendService.cs`

- `SendAsync(account, to, cc, bcc, subject, body, isHtml, attachmentPaths)` — new email
- `ReplyAsync` / `ReplyAllAsync` — set In-Reply-To/References
- `ForwardAsync` — fetch original attachments from IMAP
- **Sent folder**: `AppendToSentFolderAsync` via APPEND. Failure logged, not thrown
- **Address validation**: `ParseAndValidateAddresses` validates `local@domain` format. Throws `ArgumentException` before SMTP. Original `ParseAddresses` kept for `ReplyAllAsync`
- **Quoting**: HTML `<blockquote>`, plaintext `> ` prefix
- **Interface**: `IEmailSendService`

---

## Account Management — `AccountService.cs`

- **AddAccountAsync(emailAddress, password?, manualConfig?, authType?)**: Duplicate check → `manualConfig` or AutoDiscovery → create entity → auth type (explicit or auto-detect via `FindProviderByHost`) → OAuth or Password → validate IMAP (password) → save in transaction
- **UpdateAccountAsync**: Validate host/port → detach tracked entity → save → restart sync if active
- **DeleteAccountAsync**: Stop sync → release pool → `RemoveTokenRefreshLock` → delete attachments → detach + re-fetch → cascade delete. Null re-fetch = already deleted
- **GetAllAccountsAsync** / **GetAccountByIdAsync**: `AsNoTracking`
- **ValidateConnectionAsync**: Test IMAP
- **Deps**: `ISyncManager`, `IImapConnectionPool`
- **Interface**: `IAccountService`

---

## Background Sync — `SyncManager.cs`

- **Constants**: `IdleTimeout=29min`, `InitialReconnectDelay=1s`, `MaxReconnectDelay=300s`, `PollingInterval=59s`, `JitterFactor=0.25`, `MaxIdleFailuresBeforePolling=2`
- **Per-account**: `ConcurrentDictionary<int, (Task, CancellationTokenSource)>`
- **Watch loop** (`AccountSyncLoopAsync`): Connect → sync folders → inbox incremental (fires `NewEmailsReceived` if new messages found) → open Inbox → IDLE or poll
  - **Per-account IDLE toggle**: `account.UseIdle` (default true). When false, skips IDLE entirely and uses polling regardless of server capability. Toggled via Account Management UI
  - **IDLE path**: `IdleWaitAsync` (29min timeout, returns `bool`). Subscribes to `imapInbox.CountChanged` to cancel the done token immediately when the server pushes EXISTS (new mail), so `IdleAsync` returns within seconds instead of waiting for the full timeout. Tracks consecutive IDLE failures; 2 failures → permanently sets `supportsIdle=false` for the connection session
  - **Poll path**: `Task.Delay(PollingInterval)`
  - **STATUS after every cycle**: `imapInbox.StatusAsync(StatusItems.Count)` always sent (both IDLE and polling) to refresh folder state and reset server-side idle timer. Uses STATUS instead of NOOP because some servers (e.g. 163.com) treat NOOP-only sessions as idle and auto-logout. Also provides authoritative message count for servers that don't reliably push EXISTS (e.g. QQ Mail)
  - New messages → incremental sync → `NewEmailsReceived` event → loop
- **Backoff**: `min(1s * 2^attempt, 300s)` ± 25% jitter, min clamped to 1s
- **Network-aware**: `ManualResetEventSlim` — wait when down, unblock + reset attempt on restore
- **Auth errors**: Non-transient → stop. `OAuthReauthenticationRequiredException` → break loop
- **Shutdown**: `StopAllAsync()` cancels + awaits. `Dispose()` unsubscribes NetworkChange
- **Interface**: `ISyncManager`

---

## Concurrency Summary

| Component | Mechanism |
|-----------|-----------|
| IMAP pool | `ConcurrentDictionary` + `ConcurrentQueue` + atomic `_poolCounts` + 5min cleanup timer |
| Token refresh | Per-account `SemaphoreSlim` + double-check |
| SyncManager | `ConcurrentDictionary.GetOrAdd()` (atomic) |
| DB ops | `IDbContextFactory` → scoped DbContext per operation |
| Folder sync | Per-folder `SemaphoreSlim` (`_folderSyncLocks`) |
| EF save | `SaveChangesSafeAsync()` — detach+retry on conflict/UNIQUE |
| Batch save | Every 50 messages |
| UI | `Dispatcher.Invoke()` |
| Folder switch | `CancellationToken` cancels previous |
