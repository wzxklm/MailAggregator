# Auth Services & Security Architecture

## Credential Encryption — `Services/Auth/CredentialEncryptionService.cs`

All passwords and tokens must be encrypted before storage.

- **Scheme**: AES-256-GCM (authenticated encryption, tamper-proof)
- **Format**: `Base64(nonce[12B] + ciphertext[NB] + tag[16B])`
- **Key mgmt**: Random 256-bit key, protected via `IKeyProtector`, persisted to file
- **Safety**: Memory zeroed after encrypt/decrypt
- **Interface**: `ICredentialEncryptionService` — `Encrypt(plaintext)` / `Decrypt(ciphertext)`

## Key Protection

### `DpapiKeyProtector.cs` — Windows DPAPI
- `ProtectedData.Protect/Unprotect` (`DataProtectionScope.CurrentUser`)
- Windows only: `[SupportedOSPlatform("windows")]`

### `DevKeyProtector.cs` — Dev passthrough (insecure)
- No encryption, Linux dev/test only

## Password Auth — `Services/Auth/PasswordAuthService.cs`

- `StorePassword(account, plainPassword)` — encrypt + store to `Account.EncryptedPassword`, set `AuthType = Password`
- `RetrievePassword(account)` — decrypt to plaintext
- `HasStoredPassword(account)` / `ClearPassword(account)`
- **Interface**: `IPasswordAuthService`

## OAuth 2.0 PKCE — `Services/Auth/OAuthService.cs`

Authorization Code + PKCE flow.

- **Provider mgmt**: Loads from `oauth-providers.json`
- `FindProviderByHost(serverHost)` — match OAuth provider by IMAP/SMTP hostname
- `PrepareAuthorization(provider, loginHint?)` — generate auth URL, PKCE code_verifier, local port, redirect_uri
  - **CSRF protection**: Generates random `state` parameter (RFC 6749 Section 10.12), validated on callback
  - **TOCTOU-safe port**: `StartListenerOnFreePort()` atomically binds HttpListener with retry loop; `CleanupPendingListeners()` disposes orphaned listeners from abandoned flows
  - Pre-started listeners stored in `ConcurrentDictionary<int, (HttpListener, string State)>`
- `WaitForAuthorizationCodeAsync(port)` — retrieves pre-started HttpListener, validates `state` match, extracts code
- `ExchangeCodeForTokenAsync(provider, code, verifier, redirectUri)` — exchange code for tokens
- `RefreshTokenAsync(provider, encryptedRefreshToken)` — refresh expired tokens (60s grace period)
- **PKCE**: 128-char random code_verifier, S256 code_challenge (`Base64Url(SHA256(verifier))`)
- **Security**: Tokens encrypted before return/storage
- **invalid_grant detection**: When a token exchange or refresh returns `invalid_grant` (token revoked/expired), `OAuthService` throws `OAuthReauthenticationRequiredException` instead of a generic `HttpRequestException`. Callers (e.g., `SyncManager`) catch this to stop sync gracefully and signal the user to re-authenticate
- **Interface**: `IOAuthService`

### `OAuthReauthenticationRequiredException.cs`

Custom exception thrown when OAuth token refresh fails with `invalid_grant`. Signals that the refresh token is revoked or expired and the user must complete a full re-authorization flow.

- Thrown by `OAuthService` in `HandleTokenResponse` when response body contains `invalid_grant`
- Caught by `SyncManager.AccountSyncLoopAsync` to stop sync without retrying (non-transient auth failure)

---

## Security Architecture

### Credential encryption flow
```
Plaintext password/token
    → CredentialEncryptionService.Encrypt()
    → Generate 12B random nonce
    → AES-256-GCM encrypt
    → Output: Base64(nonce + ciphertext + tag)
    → Zero memory
    → Store to SQLite
```

### Key protection flow
```
256-bit random AES key (first-time generation, FileMode.CreateNew)
    ├─ Windows: DpapiKeyProtector → CurrentUser scope
    └─ Dev: DevKeyProtector → passthrough (insecure)
    → Persisted to %AppData%\MailAggregator\encryption.key
```

### WebView2 security
- `IsScriptEnabled = false`
- `AreDefaultContextMenusEnabled = false`
- Block all navigation to external links
- Block external resource loading (anti-tracking)
