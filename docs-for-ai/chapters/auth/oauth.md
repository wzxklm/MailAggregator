# OAuthService — OAuth 2.0 Authorization Code flow with PKCE

## Overview

Full OAuth 2.0 client implementing the Authorization Code grant with optional PKCE. Loads provider configs from a JSON file, generates authorization URLs, runs a local HTTP callback listener, exchanges codes for tokens, and refreshes expired tokens. All tokens are encrypted via `ICredentialEncryptionService` before being returned to callers.

## Key Behaviors

- **Provider lookup**: `FindProviderByHost` matches an IMAP/SMTP server hostname against `OAuthProviderConfig.ServerHosts` (case-insensitive)
- **PKCE**: Generates 128-char `code_verifier` from `[A-Za-z0-9-._~]`, computes `code_challenge = Base64Url(SHA256(verifier))`. Only included when `provider.UsePKCE` is true
- **CSRF protection**: Generates random `state` parameter (32 hex chars); validated on callback — mismatch throws `InvalidOperationException`
- **Local listener race prevention**: `PrepareAuthorization` starts `HttpListener` immediately on a free port before returning the auth URL, stored in `ConcurrentDictionary<int, (HttpListener, State)>`. `WaitForAuthorizationCodeAsync` retrieves and consumes it via `TryRemove`
- **Orphaned listener cleanup**: `CleanupPendingListeners` disposes any listeners from previously abandoned flows at the start of each new `PrepareAuthorization` call
- **Token encryption**: Both `access_token` and `refresh_token` are encrypted before being stored in `OAuthTokenResult`
- **Refresh token revocation detection**: If the token endpoint returns `invalid_grant`, throws `OAuthReauthenticationRequiredException` so callers can trigger re-auth
- **Scope reduction warning**: Logs a warning if the token response grants fewer scopes than requested (common with Microsoft providers omitting `offline_access`)

## Interface

`IOAuthService`:
- `FindProviderByHost(string serverHost)` — returns `OAuthProviderConfig?`
- `PrepareAuthorization(OAuthProviderConfig, string? loginHint)` — returns `(authorizationUrl, codeVerifier, listenerPort, redirectUri)`
- `WaitForAuthorizationCodeAsync(int listenerPort, CancellationToken)` — returns authorization code string
- `ExchangeCodeForTokenAsync(OAuthProviderConfig, string code, string codeVerifier, string redirectUri, CancellationToken)` — returns `OAuthTokenResult`
- `RefreshTokenAsync(OAuthProviderConfig, string encryptedRefreshToken, CancellationToken)` — returns `OAuthTokenResult`

## Internal Details

Constructor: `OAuthService(HttpClient, ICredentialEncryptionService, ILogger, string providersJsonPath)`

Provider config loaded from JSON at startup via `JsonSerializer.Deserialize<List<OAuthProviderConfig>>`.

`StartListenerOnFreePort`: Discovers a free port via `TcpListener(IPAddress.Loopback, 0)`, immediately starts `HttpListener` on it. Retries up to 10 times on port conflicts.

`PostTokenRequestAsync`: Shared method for both code exchange and token refresh. Sends `FormUrlEncodedContent` POST. On failure, checks for `invalid_grant` to throw `OAuthReauthenticationRequiredException`, otherwise throws `HttpRequestException`.

`ParseTokenResponse`: Extracts `access_token`, optional `refresh_token`, `expires_in` (default 3600), and `scope`. Encrypts tokens before returning.

Thread safety: `_pendingListeners` is a `ConcurrentDictionary`, safe for concurrent `PrepareAuthorization` / `WaitForAuthorizationCodeAsync` calls.

## OAuthReauthenticationRequiredException

Custom exception thrown when `RefreshTokenAsync` (or `ExchangeCodeForTokenAsync`) receives `invalid_grant` from the provider. Signals that the refresh token is revoked or expired and the user must complete the full authorization flow again. Callers (e.g. `MailConnectionHelper`, `ImapConnectionService`) catch this to trigger re-authentication UI.

## Dependencies

- Uses: `ICredentialEncryptionService`, `HttpClient`, `OAuthProviderConfig` (loaded from JSON)
- Used by: `AccountService`, `MailConnectionHelper`, `ImapConnectionService`, `SmtpConnectionService`, `AddAccountViewModel`
