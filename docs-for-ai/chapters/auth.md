# Auth Services & Security Architecture

## Credential Encryption — `CredentialEncryptionService.cs`

- **Scheme**: AES-256-GCM → `Base64(nonce[12B] + ciphertext + tag[16B])`
- **Key**: Random 256-bit, protected via `IKeyProtector`, persisted to file
- **Safety**: Memory zeroed after encrypt/decrypt
- **Interface**: `ICredentialEncryptionService` — `Encrypt(plaintext)` / `Decrypt(ciphertext)`

## Key Protection

- **`DpapiKeyProtector.cs`**: Windows DPAPI (`CurrentUser` scope), `[SupportedOSPlatform("windows")]`
- **`DevKeyProtector.cs`**: Passthrough (insecure), Linux dev/test only

## Password Auth — `PasswordAuthService.cs`

- `StorePassword(account, plainPassword)` → encrypt + store, set `AuthType = Password`
- `RetrievePassword(account)` → decrypt
- `HasStoredPassword(account)` / `ClearPassword(account)`
- **Interface**: `IPasswordAuthService`

## OAuth 2.0 PKCE — `OAuthService.cs`

- **Providers**: Loaded from `oauth-providers.json`
- `FindProviderByHost(serverHost)` — match provider by IMAP/SMTP host
- `PrepareAuthorization(provider, loginHint?)` — auth URL + PKCE + local port + redirect_uri
  - Random `state` for CSRF (RFC 6749 Section 10.12), validated on callback
  - `StartListenerOnFreePort()` atomically binds HttpListener (TOCTOU-safe); `CleanupPendingListeners()` disposes orphaned listeners
  - Pre-started listeners in `ConcurrentDictionary<int, (HttpListener, string State)>`
- `WaitForAuthorizationCodeAsync(port)` — validate state, extract code. Cancellation catches both `ObjectDisposedException` and `HttpListenerException` → converts to `OperationCanceledException`
- `ExchangeCodeForTokenAsync(provider, code, verifier, redirectUri)` — code → tokens
- `RefreshTokenAsync(provider, encryptedRefreshToken)` — refresh (60s grace)
- **PKCE**: 128-char verifier, S256 challenge (`Base64Url(SHA256(verifier))`)
- **Tokens encrypted** before return/storage
- **Scope validation**: `ParseTokenResponse` parses granted scopes, logs warning if fewer than requested. `GrantedScopes` stored in result. `provider` required (not optional)
- **invalid_grant**: Throws `OAuthReauthenticationRequiredException` → callers stop sync, signal re-auth
- **Interface**: `IOAuthService`

### `OAuthReauthenticationRequiredException.cs`
Thrown by `HandleTokenResponse` on `invalid_grant`. Caught by `SyncManager.AccountSyncLoopAsync` — stops sync without retry.

---

## Security Flows

```
Encrypt: plaintext → 12B nonce → AES-256-GCM → Base64(nonce+cipher+tag) → zero memory → SQLite
```

```
Key: 256-bit random (FileMode.CreateNew) → DpapiKeyProtector/DevKeyProtector → %AppData%\MailAggregator\encryption.key
```

**WebView2**: No scripts, no context menu, block external nav + resource loading
