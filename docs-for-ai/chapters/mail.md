# Mail Services — Discovery, Connection, Sync, Send, Account, SyncManager

## Auto-Discovery — `Services/Discovery/AutoDiscoveryService.cs`

6-level fallback to find IMAP/SMTP server config from email address:

- **Level 1**: `https://autoconfig.{domain}/mail/config-v1.1.xml` (+ HTTP fallback)
- **Level 2**: `https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml` (+ HTTP fallback)
- **Level 3**: `https://autoconfig.thunderbird.net/v1.1/{domain}` (Thunderbird ISPDB)
- **Level 4**: DNS MX query → retry Level 1-3 with MX domain
- **Level 5**: RFC 6186 SRV records (`_imaps._tcp`, `_imap._tcp`, `_submission._tcp`) — SMTP SRV query runs in parallel with IMAP queries
- **Level 6**: null (UI prompts manual config)
- **Parallel discovery**: Levels 1-3 (including HTTP fallback URLs) run in parallel via `Task.WhenAny` (Thunderbird-style `promiseFirstSuccessful`). First successful result cancels remaining requests via linked `CancellationTokenSource`
- **HTTP fallback**: Levels 1-2 also attempt HTTP URLs because many enterprise/small ISP servers only serve autoconfig over HTTP
- **XML parsing**: `ParseAutoconfigXml()` from Thunderbird-format XML. Extracts `<authentication>` element (e.g., `"OAuth2"`, `"password-cleartext"`, `"password-encrypted"`) into `ServerConfiguration.Authentication` property
- **DNS resolution**: Uses `DnsClient.NET` library (`ILookupClient`) for MX and SRV record queries. Constructor accepts `ILookupClient` for test injection. DNS timeout configured via `LookupClientOptions.Timeout` (10s)
- **MX domain extraction**: `ExtractBaseDomain()` handles ccSLDs (co.uk, com.au, etc.) via `TwoLevelTlds` set
- **Timeout**: 10s per HTTP request, 10s per DNS query
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
- **Token refresh concurrency protection**: Per-account `SemaphoreSlim` (stored in `ConcurrentDictionary<int, SemaphoreSlim>`) prevents IMAP and SMTP from refreshing the same account's token simultaneously. Uses double-check pattern: re-checks expiry after acquiring lock in case another caller already refreshed
- `RemoveTokenRefreshLock(accountId)` — disposes and removes the semaphore for a deleted account (called by `AccountService.DeleteAccountAsync` to prevent memory leaks)

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
- **Atomic pool size tracking**: `_poolCounts` (`ConcurrentDictionary<int, int>`) tracks active connections per account via `AddOrUpdate` atomic increment/decrement, preventing concurrent return-to-pool from exceeding `MaxConnectionsPerAccount`
- **Validation**: check `IsConnected && IsAuthenticated` before reuse
- **Graceful degradation**: stale connections auto-released
- **Background cleanup timer**: `Timer` fires every 5 minutes (`CleanupInterval`) to remove stale/zombie connections from all account queues. Iterates each queue, dequeues and re-enqueues live connections, disposes dead ones. Guards against `_disposed` to avoid running after pool disposal. Handles NAT/mobile networks that silently drop TCP connections
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
- Sync flags and detect deletions via `SyncFlagsAndDetectDeletionsAsync` (single IMAP FETCH for both)
- Update folder counts

### Flag sync & deletion detection (`SyncFlagsAndDetectDeletionsAsync`)
- Single IMAP FETCH (UIDs + Flags) for all locally-known messages — server returns only UIDs that still exist
- **Flag sync**: Updates `IsRead` (`\Seen`) and `IsFlagged` (`\Flagged`) properties when server flags differ from local
- **Deletion detection**: UIDs absent from server response are batch-deleted locally via `ExecuteDeleteAsync`
- Replaces the former separate `SyncExistingMessageFlagsAsync` + `DetectDeletedMessagesAsync` methods

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
  0. Check for duplicate email (+ unique DB index on `EmailAddress`) → 1. AutoDiscovery → 2. Create Account entity → 3. Check OAuth provider → 4. OAuth → set AuthType=OAuth2 / 5. Password → encrypt & store → 6. Validate IMAP (password only) → 7. Save to DB within explicit transaction (rollback on failure to prevent orphaned OAuth accounts)
- **UpdateAccountAsync** — validates host/port (non-empty host, port 1-65535 for both IMAP and SMTP), saves to DB, then restarts sync if the account is currently syncing (stop → remove pool connections → start with new config)
- **DeleteAccountAsync** — full cleanup: 1. Stop SyncManager for account → 2. Release ImapConnectionPool → 3. Remove token refresh lock (`MailConnectionHelper.RemoveTokenRefreshLock`) → 4. Delete attachment files from disk → 5. Cascade delete DB entities (account + folders + messages + attachments)
- **GetAllAccountsAsync** / **GetAccountByIdAsync** (both use `AsNoTracking` for consistency)
- **ValidateConnectionAsync** — test IMAP connection
- **Dependencies**: Injects `ISyncManager` and `IImapConnectionPool` for deletion cleanup
- **Interface**: `IAccountService`

---

## Background Sync — `Services/Sync/SyncManager.cs`

IMAP IDLE background sync orchestrator.

- **Constants**: `IdleTimeout = 29min` (RFC 2177 < 30min), `InitialReconnectDelay = 1s`, `MaxReconnectDelay = 300s`, `PollingInterval = 2min`, `JitterFactor = 0.25`
- **Concurrency**: `ConcurrentDictionary<int, (Task, CancellationTokenSource)>` per-account
- **Watch loop** (`AccountSyncLoopAsync`):
  1. Connect IMAP → 2. Sync folders → 3. Inbox incremental sync → 4. Open Inbox, check `ImapCapabilities.Idle`
  5a. **IDLE path** (server supports IDLE): `IdleWaitAsync` enters IDLE, breaks on 29min timeout or server notification. If server rejects IDLE with BAD/NO (`ImapCommandException`), falls back to polling delay for that cycle
  5b. **Polling path** (no IDLE capability): `Task.Delay(PollingInterval)` + `NoOpAsync` to refresh server state
  6. Detect new messages → incremental sync → fire `NewEmailsReceived` → re-enter loop
- **Exponential backoff with jitter**: `base = min(1s * 2^attempt, 300s)`, then randomized within +/-25% (`JitterFactor`) to prevent thundering herd. Minimum delay clamped to `InitialReconnectDelay`
- **Network-aware reconnection**: Subscribes to `NetworkChange.NetworkAvailabilityChanged`. Uses `ManualResetEventSlim` (`_networkAvailable`): when network is down, sync loops wait on `_networkAvailable.Wait()` instead of consuming backoff cycles. When network restores, `_networkAvailable.Set()` unblocks all waiting loops and resets `reconnectAttempt = 0` for immediate reconnection
- **Auth error handling**: non-transient auth errors → stop (no retry). `OAuthReauthenticationRequiredException` (invalid_grant) breaks the sync loop without retrying
- **Graceful shutdown**: `StopAllAsync()` — cancel all tokens, await all tasks
- **Disposal**: `Dispose()` unsubscribes from `NetworkChange` event and disposes `_networkAvailable`
- **Event**: `NewEmailsReceived` (`EventHandler<NewEmailsEventArgs>`)
- **Interface**: `ISyncManager`

---

## Concurrency & Thread Safety

| Component | Mechanism |
|-----------|-----------|
| IMAP pool | `ConcurrentDictionary` + `ConcurrentQueue` + `_poolCounts` atomic tracking + background `Timer` cleanup (5min) |
| Token refresh | Per-account `SemaphoreSlim` with double-check pattern (prevents IMAP/SMTP concurrent refresh) |
| SyncManager | `ConcurrentDictionary.GetOrAdd()` (atomic, no TOCTOU) |
| DB operations | `IDbContextFactory` → scoped DbContext per operation (EmailSyncService, ImapConnectionService, SmtpConnectionService) |
| EF Core save | `SaveChangesSafeAsync()` — detach + retry on conflict |
| Batch save | Every 50 messages (avoid memory bloat) |
| UI updates | `Dispatcher.Invoke()` for UI thread |
| Folder switch | `CancellationToken` cancels previous load |
