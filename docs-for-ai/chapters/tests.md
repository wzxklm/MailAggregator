# Tests — unit test suite structure, patterns, and conventions

## Test Framework Stack

| Component | Package | Version |
|-----------|---------|---------|
| Runner | xUnit | 2.5.3 |
| Mocking | Moq | 4.20.72 |
| Assertions | FluentAssertions | 8.8.0 |
| Coverage | coverlet.collector | 6.0.0 |
| SDK | Microsoft.NET.Test.Sdk | 17.8.0 |

Target: `net8.0`. Project ref: `MailAggregator.Core`. Global using: `Xunit`.

## How to Run

```bash
dotnet test src/MailAggregator.Tests/
dotnet test src/MailAggregator.Tests/ --filter "FullyQualifiedName~SyncManager"
dotnet test src/MailAggregator.Tests/ --collect:"XPlat Code Coverage"
```

## Test Organization

```
src/MailAggregator.Tests/
├── Data/
│   └── MailAggregatorDbContextTests.cs        # EF Core model CRUD, cascades, unique indexes
├── Services/
│   ├── AccountManagement/
│   │   └── AccountServiceTests.cs             # Add/update/delete/get accounts, connection validation
│   ├── Auth/
│   │   ├── CredentialEncryptionServiceTests.cs # AES-GCM round-trip, tamper detection, key persistence
│   │   ├── PasswordAuthServiceTests.cs        # Store/retrieve/clear passwords, argument validation
│   │   └── OAuthServiceTests.cs               # Provider lookup, PKCE, token exchange, refresh
│   ├── Discovery/
│   │   └── AutoDiscoveryServiceTests.cs       # 4-level fallback, XML parsing, MX domain extraction
│   ├── Mail/
│   │   ├── EmailSyncServiceTests.cs           # MapSpecialUse folder-attribute mapping
│   │   ├── EmailSendServiceTests.cs           # Reply/forward body building, address validation, MIME
│   │   └── ImapConnectionServiceTests.cs      # MailConnectionHelper encryption-type mapping
│   ├── Sync/
│   │   └── SyncManagerTests.cs                # Start/stop/lifecycle, backoff, events, graceful shutdown
│   └── TwoFactor/
│       ├── TwoFactorCodeServiceTests.cs       # TOTP generation, otpauth:// URI parsing, RFC 6238
│       └── TwoFactorAccountServiceTests.cs    # CRUD with encrypted secrets, URI import
```

## Naming Convention

`MethodName_Condition_ExpectedBehavior` — examples:
- `Encrypt_Decrypt_RoundTrip_ReturnsOriginal`
- `AddAccountAsync_WhenDiscoveryFails_ThrowsInvalidOperationException`
- `CalculateBackoffDelay_CapsAtMaxDelay`

Pattern: AAA (Arrange-Act-Assert), with `// Arrange`, `// Act`, `// Assert` comments in complex tests.

## Database Strategy

SQLite in-memory (`DataSource=:memory:`) via `DbContextOptionsBuilder.UseSqlite`. Each test class:
1. Opens connection in constructor
2. Calls `EnsureCreated()` to apply schema
3. Closes connection in `Dispose()`

Used by: `MailAggregatorDbContextTests`, `AccountServiceTests`, `TwoFactorAccountServiceTests`.

## File Inventory

| File | Tests For | Count | Key Patterns |
|------|-----------|-------|--------------|
| `Data/MailAggregatorDbContextTests.cs` | `MailAggregatorDbContext` | 6 | SQLite in-memory, cascade delete, unique index violation |
| `Auth/CredentialEncryptionServiceTests.cs` | `CredentialEncryptionService` | 8 | `DevKeyProtector`, temp dir cleanup, tamper detection, key persistence across instances |
| `Auth/PasswordAuthServiceTests.cs` | `PasswordAuthService` | 15 | Mock `ICredentialEncryptionService` (`ENC:` prefix pattern), full round-trip lifecycle |
| `Auth/OAuthServiceTests.cs` | `OAuthService` | 27+ | `CreateMockHttpHandler`, temp JSON provider file, PKCE verification, `Moq.Protected` for `HttpMessageHandler`, request body capture |
| `AccountManagement/AccountServiceTests.cs` | `AccountService` | 22 | 7 mocked dependencies, mock `ImapClient`, explicit auth-type override tests |
| `Discovery/AutoDiscoveryServiceTests.cs` | `AutoDiscoveryService` | 23+ | `MockHttpMessageHandler` (custom), `TestableAutoDiscoveryService` (overrides DNS), 4-level fallback chain, XML parsing, `ExtractBaseDomain` with ccTLD |
| `Mail/EmailSyncServiceTests.cs` | `EmailSyncService.MapSpecialUse` | 9 | Static method tests, `FolderAttributes` flag combinations |
| `Mail/EmailSendServiceTests.cs` | `EmailSendService` | 23+ | Reply/forward body construction, HTML XSS escaping, MimeKit `TextPart`/`Multipart`, address validation, temp file attachment |
| `Mail/ImapConnectionServiceTests.cs` | `MailConnectionHelper` | 5 | `Theory` with `ConnectionEncryptionType` -> `SecureSocketOptions` mapping |
| `Sync/SyncManagerTests.cs` | `SyncManager` | 30 | Blocking mock patterns (`Task.Delay(Infinite, ct)`), `TaskCompletionSource` for events, backoff jitter validation, static config assertions |
| `TwoFactor/TwoFactorCodeServiceTests.cs` | `TwoFactorCodeService` | 23+ | RFC 6238 test vectors, Base32 case-insensitivity, `otpauth://` URI parsing, algorithm/digits/period variants |
| `TwoFactor/TwoFactorAccountServiceTests.cs` | `TwoFactorAccountService` | 21 | Real `CredentialEncryptionService` + `DevKeyProtector` (not mocked), SQLite in-memory, encryption round-trip via DB |

**Total: ~237 test cases** (199 `[Fact]` + 10 `[Theory]` expanding to ~38 via `[InlineData]`)

## Common Test Patterns

### MockHttpMessageHandler (AutoDiscoveryServiceTests)

Custom `HttpMessageHandler` subclass with URL-to-response dictionary. Supports:
- `SetResponse(url, statusCode, content)` — per-URL responses
- `SetDefaultResponse(statusCode, content)` — fallback for unmatched URLs
- `SetExceptionForAll(exception)` — simulate network failures

Used to test the 4-level autoconfig fallback chain without real HTTP calls.

### TestableAutoDiscoveryService (AutoDiscoveryServiceTests)

Subclass of `AutoDiscoveryService` that overrides:
- `ResolveMxDomainAsync` — returns a preconfigured MX domain (avoids DNS)
- `TryDiscoverViaSrvAsync` — returns null (disables SRV lookup)

Enables testing Level 4 (MX fallback) and "all levels fail" scenarios.

### Mock HttpMessageHandler via Moq.Protected (OAuthServiceTests)

Uses `Moq.Protected()` to mock `HttpMessageHandler.SendAsync`:
```
mockHandler.Protected()
    .Setup<Task<HttpResponseMessage>>("SendAsync", ...)
    .Callback<HttpRequestMessage, CancellationToken>(async (req, _) => {
        capturedContent = await req.Content!.ReadAsStringAsync();
    })
```
Captures request body to verify token exchange parameters (grant_type, code_verifier, client_secret).

### Mock Encryption Service (PasswordAuthServiceTests, OAuthServiceTests)

Deterministic mock: `Encrypt` prepends `"ENC:"`, `Decrypt` strips `"ENC:"`. Enables assertion on encrypted values without real crypto.

### Real Encryption with DevKeyProtector (CredentialEncryptionServiceTests, TwoFactorAccountServiceTests)

Uses `DevKeyProtector` (no-op DPAPI substitute for non-Windows) + temp directory for key file. Tests actual AES-GCM encrypt/decrypt round-trip. Temp dir cleaned in `Dispose()`.

### Blocking Connect Pattern (SyncManagerTests)

```csharp
.Returns<LocalAccount, CancellationToken>(async (_, ct) => {
    await Task.Delay(Timeout.Infinite, ct);
    throw new OperationCanceledException(ct);
});
```
Simulates long-running IMAP IDLE to keep sync loop alive for lifecycle testing. `SetupBlockingConnect()` and `SetupBlockingFolderSync()` helper methods.

### Backoff Jitter Validation (SyncManagerTests)

Tests `SyncManager.CalculateBackoffDelay` with range assertions accounting for +/-25% jitter:
- Attempt 0: [750ms, 1250ms]
- Max cap: `MaxReconnectDelay * 1.25`
- Multiple calls produce distinct values (jitter working)

## Conventions

- **IDisposable**: Test classes using SQLite/temp files implement `IDisposable` for cleanup
- **Static helpers**: `CreateTestAccount()`, `CreateOriginalMessage()`, `CreateAccountWithFolderAsync()` — factory methods for common test entities
- **Regions**: Test files group related tests using `#region` (e.g., `#region FindProviderByHost`, `#region AddAccountAsync`)
- **Theory + InlineData**: Used for parameterized mapping/validation tests (socket types, email validation, encryption types, ccTLD extraction)
- **No test base class**: Each test class is self-contained with its own setup/teardown
