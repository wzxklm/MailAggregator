# Two-Factor Authentication — TOTP code generation and 2FA account management

## Files

| File | Responsibility |
|------|----------------|
| `src/MailAggregator.Core/Services/TwoFactor/ITwoFactorCodeService.cs` | Interface + `OtpAuthParameters` record |
| `src/MailAggregator.Core/Services/TwoFactor/TwoFactorCodeService.cs` | TOTP generation, `otpauth://` URI parsing |
| `src/MailAggregator.Core/Services/TwoFactor/ITwoFactorAccountService.cs` | Interface for 2FA account CRUD |
| `src/MailAggregator.Core/Services/TwoFactor/TwoFactorAccountService.cs` | 2FA account CRUD with encrypted secret storage |
| `src/MailAggregator.Core/Models/TwoFactorAccount.cs` | Entity: `Id`, `Issuer`, `Label`, `EncryptedSecret`, `Algorithm`, `Digits`, `Period`, timestamps |
| `src/MailAggregator.Core/Models/OtpAlgorithm.cs` | Enum: `Sha1`, `Sha256`, `Sha512` |

---

## TwoFactorCodeService

### Overview

Stateless service that generates RFC 6238 TOTP codes and parses `otpauth://totp/` URIs. Uses the `OtpNet` library for TOTP computation. All secret bytes are zeroed from memory after use.

### Key Behaviors

- **TOTP generation**: Decodes Base32 secret, creates `Totp` instance with configurable algorithm/digits/period, calls `ComputeTotp()`. Secret bytes are wiped via `CryptographicOperations.ZeroMemory` in a `finally` block.
- **Remaining seconds**: Calculates `period - (unixNow % period)` — used by UI for countdown display.
- **URI parsing**: Parses `otpauth://totp/{Issuer}:{Label}?secret=...&algorithm=...&digits=...&period=...&issuer=...`. Issuer resolved from query param first, falls back to path prefix before `:`. Secrets normalized to uppercase. Only `totp` type supported; `hotp` rejected.
- **Algorithm mapping**: Maps `OtpAlgorithm` enum to OtpNet's `OtpHashMode` (SHA1/SHA256/SHA512).
- **Validation**: Digits constrained to 6-8, period must be >= 1. Missing/invalid secret throws `FormatException`.

### Interface

`ITwoFactorCodeService` — `GenerateCode(base32Secret, algorithm?, digits?, period?)`, `GetRemainingSeconds(period?)`, `ParseOtpAuthUri(uri)`

Returns `OtpAuthParameters` record: `Secret`, `Issuer`, `Label`, `Algorithm`, `Digits`, `Period`.

### Internal Details

- `ParseQuery()`: Custom query-string parser; case-insensitive key lookup via `StringComparer.OrdinalIgnoreCase`.
- `MapAlgorithm()`: Switch expression mapping `OtpAlgorithm` -> `OtpHashMode`.
- Secret bytes allocated by `Base32Encoding.ToBytes()` are always zeroed even if `Totp` constructor throws.

### Security

- **Memory zeroing**: `CryptographicOperations.ZeroMemory(secretBytes)` in `finally` — prevents secret bytes from lingering in managed heap.
- **No secret persistence**: This service never stores secrets; it only operates on passed-in values.

### Dependencies

- Uses: `OtpNet` (NuGet)
- Used by: `TwoFactorAccountService`, `TwoFactorViewModel`, `AddTwoFactorViewModel`

---

## TwoFactorAccountService

### Overview

CRUD service for `TwoFactorAccount` entities. Secrets are encrypted at rest via `ICredentialEncryptionService` before being stored in the database. Validates secrets by attempting TOTP generation before persisting.

### Key Behaviors

- **Add (manual)**: Normalizes secret to uppercase, validates by calling `_codeService.GenerateCode()` (throws on invalid Base32), encrypts via `_encryptionService.Encrypt()`, persists to DB.
- **Add (from URI)**: Delegates to `_codeService.ParseOtpAuthUri()` then calls `AddAsync()` with parsed parameters.
- **Update**: Only `Issuer` and `Label` are mutable after creation — secret/algorithm/digits/period are immutable.
- **Delete**: Finds by ID, removes entity, saves.
- **GetAll**: Returns `AsNoTracking` list ordered by `CreatedAt`.
- **GetDecryptedSecret**: Decrypts `EncryptedSecret` on demand via `_encryptionService.Decrypt()`. Called by ViewModels when generating codes for display.

### Interface

`ITwoFactorAccountService` — `AddAsync(issuer, label, base32Secret, algorithm?, digits?, period?, ct?)`, `AddFromUriAsync(otpAuthUri, ct?)`, `UpdateAsync(id, issuer, label, ct?)`, `DeleteAsync(id, ct?)`, `GetAllAsync(ct?)`, `GetDecryptedSecret(account)`

### Internal Details

- Secret validation on add: calls `GenerateCode()` as a side-effect-free validation — if the secret is invalid Base32, OtpNet throws before any DB write.
- `GetDecryptedSecret` is synchronous (encryption/decryption is CPU-bound, no I/O).
- Entity lookup uses `FindAsync` with array syntax `[id]` for EF Core primary key lookup.

### Security

- **Encrypted at rest**: Secrets stored as `EncryptedSecret` via `ICredentialEncryptionService` (DPAPI-backed; see `chapters/auth/credential-encryption.md`).
- **Secret immutability**: After creation, the secret cannot be changed — only issuer/label are updatable. To change a secret, delete and re-add.
- **No plaintext logging**: Logger calls reference only `Issuer` and `Id`, never the secret.

### Dependencies

- Uses: `MailAggregatorDbContext`, `ICredentialEncryptionService`, `ITwoFactorCodeService`, `Serilog.ILogger`
- Used by: `TwoFactorViewModel`, `AddTwoFactorViewModel`
