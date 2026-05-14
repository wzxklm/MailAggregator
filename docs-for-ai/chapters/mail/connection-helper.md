# MailConnectionHelper — Shared connection utilities

## Overview

Internal static helper consolidating logic shared between `ImapConnectionService` and `SmtpConnectionService`: authentication (OAuth2 + password), SOCKS5 proxy setup, encryption-to-`SecureSocketOptions` mapping, retry constants, and per-account token refresh serialization. Prevents concurrent token refreshes that would cause providers like Google to invalidate refresh tokens.

## Key Behaviors

- **Unified auth**: `AuthenticateAsync` handles both `AuthType.OAuth2` (decrypt access token → `SaslMechanismOAuth2`) and password (decrypt → `client.AuthenticateAsync`). Works with any `MailService` (IMAP or SMTP).
- **Token refresh with grace period**: Refreshes tokens 60 seconds before expiry (`TokenRefreshGracePeriod`). Uses per-account `SemaphoreSlim` from `ConcurrentDictionary<int, SemaphoreSlim>` to serialize concurrent IMAP/SMTP refresh attempts. Double-checks expiry after acquiring lock.
- **Token refresh callback**: `onTokenRefreshed` delegate allows callers to persist refreshed tokens without `MailConnectionHelper` taking a DB dependency.
- **Non-transient auth detection**: `IsNonTransientAuthError` checks IMAP `NO` response text for keywords: "Unsafe Login", "Authentication", "LOGIN", "not connected". Used to skip retries on permanent auth failures.
- **Encryption mapping**: `GetSecureSocketOptions` maps `ConnectionEncryptionType` enum → MailKit `SecureSocketOptions` (`Ssl` → `SslOnConnect`, `StartTls` → `StartTls`, `None` → `None`, default → `Auto`).
- **SOCKS5 proxy**: `ConfigureProxy` sets `client.ProxyClient = new Socks5Client(host, port)` when `Account.ProxyHost` and `ProxyPort` are populated.
- **Lock cleanup**: `RemoveTokenRefreshLock(int accountId)` removes and disposes the semaphore for a deleted account. Called by `AccountService.DeleteAccountAsync`.

## Interface

All members are `internal static`:
- `AuthenticateAsync(MailService, Account, ICredentialEncryptionService, CancellationToken, IOAuthService?, onTokenRefreshed?)`
- `ConfigureProxy(MailService, Account, string protocol, ILogger)`
- `GetSecureSocketOptions(ConnectionEncryptionType)`
- `IsNonTransientAuthError(ImapCommandException)`
- `RemoveTokenRefreshLock(int accountId)`
- Constants: `MaxRetries = 3`, `InitialRetryDelay = 1s`, `TokenRefreshGracePeriod = 60s`

## Internal Details

Token refresh lock flow:
```
1. Check: token expires within 60s AND refresh token exists AND oAuthService != null
2. Acquire per-account SemaphoreSlim
3. Re-check expiry (another caller may have refreshed)
4. oAuthService.RefreshTokenAsync → update Account fields
5. onTokenRefreshed callback (persist to DB)
6. Release semaphore
```

`_tokenRefreshLocks` is a static `ConcurrentDictionary<int, SemaphoreSlim>` — lives for process lifetime. `RemoveTokenRefreshLock` prevents leak on account deletion.

## Dependencies

- Uses: `ICredentialEncryptionService` (decrypt tokens/passwords), `IOAuthService` (token refresh, provider lookup)
- Used by: `ImapConnectionService`, `SmtpConnectionService`, `SyncManager` (non-transient auth check), `AccountService` (lock cleanup)
