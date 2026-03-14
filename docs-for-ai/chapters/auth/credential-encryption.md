# CredentialEncryptionService — AES-256-GCM encryption for all stored credentials

## Overview

Central encryption service used by every component that stores or retrieves secrets (passwords, OAuth tokens). Uses AES-256-GCM with random nonces. The 256-bit master key is generated once, persisted to a file, and protected at rest by an `IKeyProtector` (DPAPI on Windows, no-op on Linux dev).

## Key Behaviors

- **Storage format**: `Base64(nonce[12] + ciphertext[N] + tag[16])` — single opaque string per credential
- **Random nonce**: Every `Encrypt` call generates a fresh 12-byte nonce via `RandomNumberGenerator`, so encrypting the same plaintext twice yields different ciphertext
- **Memory hygiene**: Plaintext byte arrays are zeroed via `CryptographicOperations.ZeroMemory` in `finally` blocks
- **Atomic key creation**: Uses `FileMode.CreateNew` to avoid TOCTOU race where two processes could generate different keys; if the file already exists, falls back to reading it

## Interface

`ICredentialEncryptionService` — `Encrypt(string plaintext)`, `Decrypt(string ciphertext)`

## Internal Details

Constructor: `CredentialEncryptionService(IKeyProtector keyProtector, string keyFilePath)`

`LoadOrCreateKey` flow:
1. Ensure key file directory exists
2. Generate 32-byte random key, protect via `IKeyProtector.Protect`, write with `FileMode.CreateNew`
3. If `IOException` (file exists) — read file, unprotect via `IKeyProtector.Unprotect`

Constants: `NonceSize=12`, `TagSize=16`, `KeySize=32`

## Key Protectors

### IKeyProtector

Interface: `Protect(byte[] data)`, `Unprotect(byte[] data)`

Abstracts platform-specific protection of the AES master key at rest.

### DpapiKeyProtector (production, Windows only)

- Annotated `[SupportedOSPlatform("windows")]`
- Delegates to `ProtectedData.Protect/Unprotect` with `DataProtectionScope.CurrentUser`
- Key is bound to the Windows user profile — other users/machines cannot decrypt

### DevKeyProtector (dev/test, Linux only)

- Pass-through (returns data unchanged) — **NOT secure**
- Exists solely because DPAPI is unavailable on Linux
- Registered via DI only in development configuration

## Dependencies

- Uses: `IKeyProtector` (injected — `DpapiKeyProtector` or `DevKeyProtector`)
- Used by: `PasswordAuthService`, `OAuthService`, `MailConnectionHelper`, `TwoFactorAccountService`, `ImapConnectionService`, `SmtpConnectionService`
