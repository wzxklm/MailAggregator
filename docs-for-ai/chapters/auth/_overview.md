# Auth — Credential encryption, password auth, and OAuth 2.0

## Files

| File | Chapter | Responsibility |
|------|---------|----------------|
| `src/MailAggregator.Core/Services/Auth/ICredentialEncryptionService.cs` | [credential-encryption.md](credential-encryption.md) | Encrypt/decrypt interface |
| `src/MailAggregator.Core/Services/Auth/CredentialEncryptionService.cs` | [credential-encryption.md](credential-encryption.md) | AES-256-GCM encryption for passwords and tokens |
| `src/MailAggregator.Core/Services/Auth/IKeyProtector.cs` | [credential-encryption.md](credential-encryption.md) | Platform-specific key protection interface |
| `src/MailAggregator.Core/Services/Auth/DpapiKeyProtector.cs` | [credential-encryption.md](credential-encryption.md) | Windows DPAPI key protection (production) |
| `src/MailAggregator.Core/Services/Auth/DevKeyProtector.cs` | [credential-encryption.md](credential-encryption.md) | No-op key protection (dev/Linux only) |
| `src/MailAggregator.Core/Services/Auth/IPasswordAuthService.cs` | [password-auth.md](password-auth.md) | Password storage interface |
| `src/MailAggregator.Core/Services/Auth/PasswordAuthService.cs` | [password-auth.md](password-auth.md) | Encrypt/store/retrieve account passwords |
| `src/MailAggregator.Core/Services/Auth/IOAuthService.cs` | [oauth.md](oauth.md) | OAuth flow interface |
| `src/MailAggregator.Core/Services/Auth/OAuthService.cs` | [oauth.md](oauth.md) | OAuth 2.0 Authorization Code + PKCE flow |
| `src/MailAggregator.Core/Services/Auth/OAuthReauthenticationRequiredException.cs` | [oauth.md](oauth.md) | Thrown when refresh token is revoked/expired |

## Overview

All credentials (passwords, OAuth access/refresh tokens) are encrypted at rest before storage. `PasswordAuthService` and `OAuthService` both delegate to `CredentialEncryptionService` for encrypt/decrypt, ensuring a single encryption path for all secrets. See [credential-encryption.md](credential-encryption.md) for encryption internals.
