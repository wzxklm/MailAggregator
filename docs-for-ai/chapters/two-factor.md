# Two-Factor Auth — TOTP Authenticator

Local TOTP (RFC 6238), no network. OtpNet 1.4.0. TOTP only. Secrets: AES-256-GCM encrypted.

Data models & DB: see `core-data.md` (`OtpAlgorithm`, `TwoFactorAccount`, DbSet, CREATE TABLE).

---

## Code Service — `TwoFactorCodeService.cs` (Singleton)

- **Record**: `OtpAuthParameters(Secret, Issuer, Label, Algorithm, Digits, Period)` in `ITwoFactorCodeService.cs`

| Method | Behavior |
|--------|----------|
| `GenerateCode(base32Secret, algorithm, digits, period)` | Base32 decode → OtpNet `Totp` → compute → `ZeroMemory` secret bytes |
| `GetRemainingSeconds(period)` | `period - (unixTimestamp % period)` |
| `ParseOtpAuthUri(uri)` | `otpauth://totp/` → validate scheme/host, parse `/Label` or `/Issuer:Label`, query params (secret required). Uppercase normalize. Case-insensitive keys |

Helpers: `MapAlgorithm()` (enum→`OtpHashMode`), `ParseQuery()` (case-insensitive)

---

## Account Service — `TwoFactorAccountService.cs` (Scoped)

Deps: `DbContext`, `ICredentialEncryptionService`, `ITwoFactorCodeService`, `ILogger`

| Method | Behavior |
|--------|----------|
| `AddAsync(issuer, label, base32Secret, algorithm, digits, period)` | Validate → uppercase secret → test generate → encrypt → save |
| `AddFromUriAsync(otpAuthUri)` | Parse → delegate to `AddAsync()` |
| `UpdateAsync(id, issuer, label)` | Issuer/label only (secret immutable) |
| `DeleteAsync(id)` | Find + remove |
| `GetAllAsync()` | `AsNoTracking`, ordered by `CreatedAt` |
| `GetDecryptedSecret(account)` | Decrypt via `ICredentialEncryptionService` |

---

## Cross-References

UI: `desktop.md` | Workflows: `workflows.md` (sections 6-7) | Tests: `tests.md`

Security: Encrypted at rest (AES-256-GCM). Memory: `ZeroMemory` after use (see `pitfalls.md`).
