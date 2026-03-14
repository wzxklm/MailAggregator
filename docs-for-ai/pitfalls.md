# Pitfalls & Conventions

> Must-read before coding.

---

## Core Data (EF Core / SQLite)

- **DateTimeOffset storage**: SQLite lacks native `DateTimeOffset`; all properties converted to UTC ticks (`long`) via `DateTimeOffsetToLongConverter`. Transparent but means raw SQL queries must use tick values, not date strings
- **Timestamp auto-stamping**: `SaveChanges`/`SaveChangesAsync` auto-sets `CreatedAt`/`UpdatedAt` on Account and TwoFactorAccount, `CachedAt` on EmailMessage. Do not set these manually
- 🔴 **Entity tracking conflicts**: Root-scoped `DbContext` in `AccountService` can track stale entities. Always use `AsNoTracking()` for reads; explicitly detach + re-fetch before updates/deletes to avoid `DbUpdateConcurrencyException`
- 🔴 **Cascade delete + disk files**: Deleting an Account cascades DB rows (Folders → Messages → Attachments), but attachment files on disk must be deleted manually first. `AccountService.DeleteAccountAsync()` handles this but partial failure leaves orphaned files
- **Unique constraints**: `(AccountId, FullName)` on folders, `(FolderId, Uid)` on messages. Violating these throws `SqliteException` (error code 19)

## Authentication & Encryption

- 🔴 **DevKeyProtector is NO-OP**: Returns data unchanged — zero encryption. Only for dev/test on Linux. Never use in production; credentials stored in plaintext on disk
- **AES-256-GCM format**: Encrypted data = Base64(12-byte nonce + ciphertext + 16-byte tag). Do not modify format without updating both Encrypt and Decrypt
- **Memory zeroing**: `CredentialEncryptionService` zeros plaintext buffers with `CryptographicOperations.ZeroMemory`. Maintain this pattern for any new encryption code
- **DPAPI scope**: `DpapiKeyProtector` uses `DataProtectionScope.CurrentUser`. Master key is user-bound — cannot decrypt on another Windows account or machine
- 🔴 **Orphaned OAuth listeners**: Abandoned OAuth flows (user cancels) leave `HttpListener` resources. Cleaned up on next `PrepareAuthorization()` call, not on timeout/crash
- **OAuth scope reduction**: If provider grants fewer scopes than requested (e.g., Microsoft omits `offline_access`), OAuthService logs warning but proceeds. Token refresh may fail silently later
- **Token refresh serialization**: Per-account semaphore prevents IMAP + SMTP from refreshing simultaneously — providers like Google invalidate old tokens on concurrent refresh

## Account Management

- 🔴 **OAuth token atomicity gap**: Account saved to DB before OAuth token exchange completes (exchange happens in UI post-save). If token exchange fails, account exists without tokens — unusable until re-auth
- **Concurrent update safety**: `UpdateAccountAsync` and `DeleteAccountAsync` detach tracked entities and re-fetch to prevent concurrency exceptions from long-lived DbContext

## Server Discovery

- **ccSLD hardcoding**: `TwoLevelTlds` static set (40+ entries: co.uk, com.br, etc.) is manually maintained. New country-code SLDs require code update; no heuristic fallback
- **Malformed XML silent failure**: `ParseAutoconfigXml()` returns null on XML parse errors. Bad autoconfig XML from a server looks identical to "no config found"
- **10-second timeouts stack**: Each discovery level can take up to 10s (DNS/HTTP). Worst case: 6 levels × 10s = 60s total discovery time

## Mail (Connection / Sync / Send)

- 🔴 **Reflection-based folder injection**: `ImapFolderDiscovery` injects root folder into MailKit's internal `ImapEngine.FolderCache` via reflection for NIL NAMESPACE servers. Breaks if MailKit changes internal API. Fallback strategies exist but are less reliable
- 🔴 **UIDVALIDITY reset destroys messages**: When server changes UIDVALIDITY (maintenance, migration), all local folder messages are bulk-deleted and re-synced. Correct per RFC but destructive
- 🔴 **Token refresh semaphore leak**: `_tokenRefreshLocks` in `MailConnectionHelper` accumulates per-account semaphores. `RemoveTokenRefreshLock()` must be called on account deletion (done in `AccountService.DeleteAccountAsync`). Missing this call leaks memory
- 🔴 **OAuth expiry uses local clock**: `DateTimeOffset.UtcNow` compared to token expiry. Auth failures if client clock is behind server by more than 60s grace period
- **Per-folder sync locks**: Must acquire per-folder semaphore before syncing. Without it, concurrent sync causes `(FolderId, Uid)` UNIQUE constraint violation
- **IOException retry**: Pooled connections retry once on IOException (dead socket) in both `EmailSyncService` and `EmailOperationService`. No exponential backoff between retries — assumes fresh connection succeeds
- **Sent folder append non-fatal**: If appending sent email to Sent folder fails (missing folder, network error), logs warning and continues. User sees email sent but no local Sent copy
- **Forward without original**: If fetching original message fails during forward, silently proceeds without original attachments/body

## Sync Manager

- **STATUS vs NOOP**: Must use STATUS (not NOOP) to reset server idle timer — see `chapters/sync.md` for details
- **IDLE 29-min timeout**: Connection resets every 29min per RFC 2177 — do not increase
- **Permanent polling fallback**: Permanently switches to polling after consecutive IDLE rejections. Restart account sync to retry IDLE
- **Network detection coarse-grained**: `NetworkChange` events fire on any network state change (including LAN). May cause premature reconnect signals
- **Per-folder lock accumulation**: `_folderSyncLocks` in `EmailSyncService` never cleaned up when folders deleted. Long-running app accumulates unused semaphores

## Two-Factor (TOTP)

- **No recovery for deleted accounts**: 2FA accounts permanently removed from DB on delete. No soft-delete or export mechanism
- **Decryption on-demand**: `GetDecryptedSecret()` decrypts every call. Callers should cache result if needed multiple times per operation
- **Secret case normalization**: Base32 secrets normalized to uppercase on storage. Transparent but differs from raw URI input

## Desktop (WPF UI)

- 🔴 **PasswordBox code-behind binding**: WPF `PasswordBox.Password` cannot be XAML-bound for security. `AddAccountWindow.xaml.cs` uses code-behind event handler. Password held unencrypted in ViewModel during wizard
- **Decrypted secrets in memory**: `TwoFactorDisplayItem` holds decrypted TOTP secrets in memory for code generation. Cleared when `TwoFactorViewModel.Dispose()` is called (window close)
- **WebView2 optional**: If WebView2 runtime not installed, HTML preview unavailable. App logs warning and continues
- **Dispatcher marshalling required**: All `ISyncManager` events fire on background threads. UI updates must use `Dispatcher.Invoke()` / `InvokeAsync()`. `MainViewModel` handles this correctly — maintain pattern for new event handlers
- **CancellationTokenSource for stale loads**: `MainViewModel` cancels in-flight folder/message loads when user switches context. Always cancel previous CTS before starting new async operation

## Architecture Conventions

- **DI registration**: All services registered in `App.xaml.cs`. Core services are singleton; `DbContext` uses factory pattern (`IDbContextFactory`) for long-running operations
- **Interface-first**: Every service has an `I{ServiceName}` interface. Mock via interface in tests
- **Serilog structured logging**: Use `Log.ForContext<T>()` pattern. Never log decrypted credentials or tokens
- **Async all the way**: All I/O operations are async. Never use `.Result` or `.Wait()` on tasks
- **ViewModel dialog pattern**: ViewModels expose `CloseRequested` event. Views subscribe and set `DialogResult`. Modal dialogs use `ShowDialog()`, modeless use `Show()`
