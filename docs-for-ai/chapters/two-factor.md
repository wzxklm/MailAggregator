# Two-Factor Auth — TOTP Authenticator

Integrated TOTP authenticator (RFC 6238) in MailAggregator. Pure local computation, no network required. Secrets encrypted with existing AES-256-GCM encryption service. Uses **OtpNet 1.4.0** NuGet package. TOTP only (no HOTP).

---

## Data Models — `Models/`

### `OtpAlgorithm.cs` — HMAC algorithm enum
- `Sha1` (default), `Sha256`, `Sha512`

### `TwoFactorAccount.cs` — 2FA account entity
- `Id` (PK), `Issuer` (required, max 256), `Label` (required, max 256)
- `EncryptedSecret` — Base32 secret stored encrypted via `ICredentialEncryptionService`
- `Algorithm` (default: Sha1), `Digits` (default: 6), `Period` (default: 30s)
- `CreatedAt`, `UpdatedAt` — auto-stamped by `StampTimestamps()`
- **Independent**: No foreign key to `Account` (standalone entity)

---

## Code Service — `Services/TwoFactor/TwoFactorCodeService.cs`

Stateless TOTP code generator. DI lifetime: **Singleton**.

- **Record**: `OtpAuthParameters(Secret, Issuer, Label, Algorithm, Digits, Period)` — defined in `ITwoFactorCodeService.cs`
- **Interface**: `ITwoFactorCodeService`

**Key methods**:
| Method | Behavior |
|--------|----------|
| `GenerateCode(base32Secret, algorithm, digits, period)` | Decode Base32 → create OtpNet `Totp` → compute code → `CryptographicOperations.ZeroMemory()` on secret bytes |
| `GetRemainingSeconds(period)` | `period - (unixTimestamp % period)` |
| `ParseOtpAuthUri(uri)` | Parse `otpauth://totp/` URI: scheme/host validation, path as `/Label` or `/Issuer:Label`, query params (secret required, issuer, algorithm, digits 6-8, period). Secret normalized to uppercase. Case-insensitive query keys |

**Private helpers**: `MapAlgorithm()` (enum → `OtpHashMode`), `ParseQuery()` (case-insensitive query string parser)

---

## Account Service — `Services/TwoFactor/TwoFactorAccountService.cs`

CRUD for 2FA accounts with encrypted secret storage. DI lifetime: **Scoped** (depends on DbContext).

- **Dependencies**: `MailAggregatorDbContext`, `ICredentialEncryptionService`, `ITwoFactorCodeService`, `ILogger`
- **Interface**: `ITwoFactorAccountService`

**Key methods**:
| Method | Behavior |
|--------|----------|
| `AddAsync(issuer, label, base32Secret, algorithm, digits, period)` | Validate inputs → normalize secret to uppercase → validate by generating test code → encrypt secret → save to DB |
| `AddFromUriAsync(otpAuthUri)` | `ParseOtpAuthUri()` → delegate to `AddAsync()` |
| `UpdateAsync(id, issuer, label)` | Update issuer/label only (secret is immutable) |
| `DeleteAsync(id)` | Find and remove account |
| `GetAllAsync()` | `AsNoTracking()`, ordered by `CreatedAt` |
| `GetDecryptedSecret(account)` | Decrypt `EncryptedSecret` via `ICredentialEncryptionService` |

---

## Database — Data Layer Changes

### `MailAggregatorDbContext.cs`
- **DbSet**: `TwoFactorAccounts` (expression-bodied `Set<TwoFactorAccount>()`)
- **Entity config** (`OnModelCreating`): PK `Id`, required `Issuer` (max 256), required `Label` (max 256), required `EncryptedSecret`
- **Auto timestamps**: `StampTimestamps()` handles `TwoFactorAccount` — sets `CreatedAt`/`UpdatedAt` on insert, `UpdatedAt` on modify
- **Value conversion**: `DateTimeOffset` stored as UTC ticks (long) via `DateTimeOffsetToLongConverter`

### `DatabaseInitializer.cs`
- `EnsureCreatedAsync()` only creates tables for new databases; for existing databases, explicit `CREATE TABLE IF NOT EXISTS TwoFactorAccounts` is required
- Schema: `Id` (INTEGER PK AUTOINCREMENT), `Issuer`/`Label`/`EncryptedSecret` (TEXT NOT NULL), `Algorithm` (INTEGER DEFAULT 0), `Digits` (INTEGER DEFAULT 6), `Period` (INTEGER DEFAULT 30), `CreatedAt`/`UpdatedAt` (INTEGER DEFAULT 0)

---

## Desktop UI

UI layer is documented in `desktop.md` under dedicated sections:
- **TwoFactorWindow** — non-modal window, ListBox with real-time codes, DispatcherTimer (1s)
- **TwoFactorViewModel** — scope-based `ITwoFactorAccountService` resolution, timer lifecycle
- **AddTwoFactorWindow** — modal dialog, manual input / URI import / edit modes
- **AddTwoFactorViewModel** — `CloseRequested` event pattern, validation
- **TwoFactorDisplayItem** — `UpdateCode()` at period boundary, code formatting ("123 456" / "1234 5678")

**DI registrations** (in `App.xaml.cs`):
| Service | Lifetime |
|---------|----------|
| `ITwoFactorCodeService` | Singleton |
| `ITwoFactorAccountService` | Scoped |
| `TwoFactorViewModel` | Transient |
| `AddTwoFactorViewModel` | Transient |

**MainWindow integration**: Toolbar "2FA" button → `OpenTwoFactorCommand` in `MainViewModel`

---

## Tests

| Test File | Count | Coverage |
|-----------|-------|----------|
| `Services/TwoFactor/TwoFactorCodeServiceTests.cs` | 23 | 6/8-digit output, SHA1/256/512, null/empty validation, case-insensitive secrets, memory zeroization, URI parsing (full/minimal/encoded/error cases), secret normalization |
| `Services/TwoFactor/TwoFactorAccountServiceTests.cs` | 17 | CRUD operations, encryption round-trip, input validation, URI integration, ordered retrieval, `GetDecryptedSecret` |

**Test patterns**: In-memory SQLite, `DevKeyProtector` for test encryption, Moq for `ILogger`, FluentAssertions

---

## Security

- **Encryption at rest**: Secrets encrypted via `ICredentialEncryptionService` (AES-256-GCM) before DB storage
- **Memory safety**: `CryptographicOperations.ZeroMemory()` on decoded secret bytes after code generation
- **Secret immutability**: Edit mode only allows changing Issuer/Label, never the secret
