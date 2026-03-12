# Pitfalls & Conventions

> Must-read before coding.

---

## EF Core / Database

- **DbContext not thread-safe**: Use `ToListAsync()`, never `Task.Run(() => query.ToList())`. Background services use `IDbContextFactory`; UI-layer scoped services inject directly
- **Concurrency conflicts**: Entity from one DbContext cannot Attach to another. `SaveChangesSafeAsync()` handles this (detach + retry) and SQLite UNIQUE violations (error 19, detach Added + retry)
- **No concurrent sync on same folder**: `EmailSyncService` uses per-folder `SemaphoreSlim` (`_folderSyncLocks`). Locks in public methods; `*CoreAsync` are lock-free
- **UIDVALIDITY must call CoreAsync**: `SyncIncrementalCoreAsync` on UIDVALIDITY change must call `SyncInitialCoreAsync` (not public `SyncInitialAsync`) to avoid SemaphoreSlim deadlock. Also reuses caller's ImapClient (pool max 2)
- **Check ChangeTracker before Attach**: After save, entities remain tracked. Attach on same Id throws. Fix: query `ChangeTracker.Entries<T>()` dict first — update tracked entity directly or Attach new stub. See `SetMessageReadAsync`, `FetchMessageBodyAsync`, `SyncFlagsAndDetectDeletionsAsync`
- **Avoid `Update()` cascade**: `Update(folder)` cascades to `Messages` nav property causing conflicts. Use `Attach` + `Property(...).IsModified = true`
- **Batch saves**: Save every 50 items during bulk sync
- **New entities need manual table creation**: `EnsureCreatedAsync()` only works for new DBs. Add `ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS ...")` in `DatabaseInitializer.InitializeAsync`. Types: `DateTimeOffset` → `INTEGER` (UTC ticks), enums → `INTEGER`
- **Schema migration catch must be narrowed**: `ALTER TABLE ... ADD COLUMN` throws `SqliteException` if the column exists. Catch only when `ex.SqliteErrorCode == 1` (SQLITE_ERROR). A broad `catch (SqliteException)` would silently swallow I/O and corruption errors
- **New timestamped entities**: Add `ChangeTracker.Entries<T>()` loop in `StampTimestamps()` for `CreatedAt`/`UpdatedAt` auto-population

## MailKit / Mail Protocol

- **Naming conflict — MailFolder**: `MailKit.MailFolder` vs `Models.MailFolder` → use `using LocalMailFolder = MailAggregator.Core.Models.MailFolder`
- **Naming conflict — Account**: `Services.AccountManagement` namespace → use `using LocalAccount = MailAggregator.Core.Models.Account`
- **IMAP ID (RFC 2971) before auth**: 163.com/Coremail requires `IdentifyAsync` before login. Only send when `ImapCapabilities.Id` present; failure is non-fatal (log and swallow)
- **IMAP IDLE needs dedicated connection**: IDLE occupies connection. Pool max 2 per account
- **IDLE timeout**: 29min (RFC 2177 < 30min). Reopen folder before re-entering IDLE
- **MailKit `IdleAsync` does not auto-return on new mail**: `IdleAsync(doneToken, ct)` only exits when `doneToken` is cancelled — server EXISTS notifications update `folder.Count` internally but do NOT break out of IDLE. Must subscribe to `folder.CountChanged` and cancel the done token in the handler, otherwise IDLE blocks for the full timeout (29min) even when mail arrives
- **Validate pooled connections**: Check `IsConnected && IsAuthenticated` before reuse; release stale ones
- **Microsoft "not connected" non-transient error**: `IsNonTransientAuthError` detects this. `ImapConnectionService` retry loop rethrows immediately
- **MimeKit TryParse doesn't validate format**: Accepts bare numbers without domain. Use `ParseAndValidateAddresses` to verify `local@domain` before SMTP send

## OAuth

- **PKCE per-provider**: `usePKCE` in `oauth-providers.json` — do not hardcode
- **RedirectionEndpoint per-provider**: Yahoo/AOL require fixed redirect URI → use `redirectionEndpoint` field
- **Persist RefreshToken immediately after refresh**: `PersistRefreshedTokenAsync` — old token invalidated on refresh
- **Token refresh grace period**: 60s before expiry (`TokenRefreshGracePeriod`)
- **Token refresh concurrency**: Per-account `SemaphoreSlim` + double-check in `MailConnectionHelper`. On account delete, call `RemoveTokenRefreshLock`
- **invalid_grant not retryable**: `OAuthReauthenticationRequiredException` → `SyncManager` breaks sync loop. User must re-authorize
- **OAuth detection driven by ViewModel**: `AddAccountAsync` takes explicit `authType` from ViewModel. Do not override in AccountService — causes "OAuth2 but no token" bug

## WPF / UI

- **Styles in `Resources/Styles.xaml` only**: No styles/converters in individual Window files
- **Unsubscribe singleton events**: ViewModels subscribing to events like `SyncManager.NewEmailsReceived` must `IDisposable` + unsubscribe, else memory leak
- **UI thread updates**: ObservableCollection changes from background → `Dispatcher.Invoke()`
- **Per-account error isolation**: Multi-account loops must try/catch per account (one failure must not block others)
- **`UseWindowsForms=true` required**: NotifyIcon depends on WinForms
- **WinForms type ambiguity**: `MessageBox`, `Clipboard` exist in both namespaces. Use `System.Windows.MessageBox.Show()`, `System.Windows.Clipboard.SetText()` (CS0104)
- **`BoolToVisibilityConverter` for Visibility only**: Returns `Visibility` enum, not for `bool?` bindings like `RadioButton.IsChecked`
- **ModernWpf styles by key**: Reference base styles as `BasedOn="{StaticResource AccentButtonStyle}"`, `DefaultButtonStyle`, `DefaultListBoxItemStyle` — not by TargetType
- **Use semantic color resources**: Use `SuccessBackgroundBrush`, `ErrorBackgroundBrush`, `InfoBackgroundBrush`, `SubtleBrush`, `CardBrush`, `CardBorderBrush` instead of hardcoding hex colors
- **Icon buttons use Segoe MDL2 Assets**: Use separate `TextBlock` elements for icon (FontFamily="Segoe MDL2 Assets") and label text — do not mix FontFamily in one element
- **`FallbackValue=Collapsed` for optional bindings**: When binding to a property that may not exist on the DataContext (e.g. `UnreadCount` on `AccountFolderNode`), add `FallbackValue=Collapsed` — otherwise WPF passes `DependencyProperty.UnsetValue` (not null) to converters, causing unexpected visibility
- **Minimize-to-tray close guard**: `MainWindow.OnClosing` must check `NotificationHelper.IsExitRequested` — without it, the app becomes unkillable (close always hides). Tray Exit sets flag before `Application.Current.Shutdown()`
- **Embedded resource naming**: `GetManifestResourceStream` uses `{AssemblyName}.{Path.With.Dots}` — e.g. `Resources\app.ico` → `"MailAggregator.Desktop.Resources.app.ico"`. Folder separators become dots

## Mail Discovery & Sync

- **MX domain extraction handles ccSLDs**: `co.uk`, `com.au` → extract 3rd-level domain via `TwoLevelTlds` HashSet
- **DNS via DnsClient.NET**: `ILookupClient` (injectable). 10s timeout
- **SRV discovery (Level 5)**: `_imaps._tcp`→`_imap._tcp`→`_submission._tcp`; SMTP SRV in parallel
- **Pass `manualConfig` to skip redundant discovery**: ViewModel already has config → pass via `manualConfig`, else AccountService re-runs discovery
- **Account deletion cleanup order**: `StopAccountSyncAsync` → `RemoveAccount` (pool) → `RemoveTokenRefreshLock` → delete attachments → delete DB
- **Detach tracked Account before Update/Delete**: Root DbContext may track stale entity. Update: detach via `ChangeTracker.Entries<Account>()`. Delete: `Detached` → re-fetch → `Remove(freshAccount)`
- **Account update restarts sync**: Validates host/port, then stop → remove pool → restart if syncing
- **Per-account IDLE toggle**: `Account.UseIdle` (default true). SyncManager checks `account.UseIdle && ImapCapabilities.Idle` — when false, always polls. Toggled via AccountListViewModel `ToggleIdleCommand` → `UpdateAccountAsync` (restarts sync)
- **IDLE fallback with permanent degradation**: Check `ImapCapabilities.Idle`. No IDLE → 59s polling. IDLE rejection (`ImapCommandException`) → poll delay + increment failure counter; 2 consecutive failures → permanently switch to polling for that connection session. `StatusAsync(StatusItems.Count)` always sent after both IDLE and poll cycles — uses STATUS instead of NOOP because some servers (e.g. 163.com) treat NOOP-only sessions as idle and auto-logout; also provides authoritative message count for servers that don't reliably push EXISTS (e.g. QQ Mail)
- **Folder loading: DB-first, IMAP only once**: Folders are synced from IMAP only on first account connection (when DB has no folders). After that, folders are always read from the local database via `GetFoldersFromDbAsync`. `MainViewModel.LoadAccountsAsync` and `SyncManager.AccountSyncLoopAsync` both use DB reads — no IMAP connection needed for folder loading after initial sync
- **Dead pooled connections — IOException retry**: Pooled connections may appear alive (`IsConnected=true`) but have dead TCP sockets (silently dropped by firewall/NAT). `SyncFoldersAsync`, `SyncIncrementalAsync` and `FetchMessageBodyAsync` retry once on `IOException` — first failure disposes the bad connection, retry gets a fresh one
- **Expired OAuth tokens in pool**: `GetConnectionAsync` checks `account.OAuthTokenExpiry` before reusing a pooled connection. Expired → discard and create fresh connection (triggers token refresh in `ConnectAsync`). Without this, servers reject with `ImapProtocolException: Session invalidated - AccessTokenExpired`
- **Merged flag sync + deletion**: `SyncFlagsAndDetectDeletionsAsync` — single FETCH (missing UIDs = deleted)
- **Pool cleanup timer**: 5min `Timer` removes zombie connections (NAT/mobile silent TCP drops)
- **Pool size atomic tracking**: `_poolCounts` `AddOrUpdate` prevents exceeding limit
- **Backoff jitter**: ±25% (`JitterFactor=0.25`) prevents thundering herd
- **Network-aware reconnection**: `ManualResetEventSlim` (`_networkAvailable`): wait when down, immediate reconnect (reset attempt=0) on restore. Use `_networkAvailable.IsSet` directly
- **AutoDiscovery cancels on first success**: L1-3 parallel → first success → `CancelAsync()` others
- **Discovery skip CancellationTokenSource lifecycle**: `AddAccountViewModel._discoveryCts` must be Cancel+Dispose+null before replacement. `SkipDiscovery` also resets `IsDiscoverySucceeded = false` to prevent stale state from a previous successful discovery affecting `GoBack` navigation
- **PersonalNamespaces may be empty**: Some IMAP servers return empty `PersonalNamespaces` and don't support `GetFolder("")` or `GetFoldersAsync` with constructed namespaces. `SyncFoldersCoreAsync` falls back to `client.Inbox` as the sole folder — guaranteed by IMAP spec

## 2FA / TOTP

- **`FindAsync` for PK lookups**: Checks ChangeTracker first → avoids tracked-entity conflicts. Used in `UpdateAsync`/`DeleteAsync`
- **Normalize secrets `ToUpperInvariant()`**: Base32 case-insensitive; normalize before validation/encryption. `ParseOtpAuthUri` also normalizes
- **Zero secret bytes after use**: `CryptographicOperations.ZeroMemory` in `try/finally`

## Architecture Conventions

- **`MailConnectionHelper` centralizes connection logic**: Auth, proxy, encryption mapping, `IsNonTransientAuthError` — do not duplicate or inline
- **New services**: Register in `App.xaml.cs` + define `I`-prefixed interface
- **Test structure mirrors Core**: `Tests/Services/` mirrors `Core/Services/` 1:1

## Security

- **Encrypt all sensitive data**: Passwords, tokens → `CredentialEncryptionService` (AES-256-GCM). Never store plaintext
- **Key protection**: Prod: `DpapiKeyProtector` (CurrentUser). Dev: `DevKeyProtector` (passthrough)
- **WebView2 hardened**: No scripts, no context menu, block external nav + resources
- **OAuth state**: `RandomNumberGenerator` random state, verify on callback (CSRF prevention)
- **DNS via library only**: DnsClient.NET — no external process (no injection risk)
- **HttpListener cancellation catches two types**: `ObjectDisposedException` or `HttpListenerException` (995) → convert to `OperationCanceledException`
- **HttpListener cleanup**: `_pendingListeners` cleaned on new OAuth flow (prevent port leaks)
- **Port TOCTOU**: `StartListenerOnFreePort` binds immediately with retry

## Build & Test

- **Core**: `net8.0` — cross-platform, builds on Linux
- **Desktop**: `net8.0-windows` — Windows only
- **Test**: `dotnet test src/MailAggregator.Tests/` — run after every change
