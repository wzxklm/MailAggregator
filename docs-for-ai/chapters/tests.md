# Tests

## Config

**`MailAggregator.Tests.csproj`**: xUnit 2.5.3 + Moq 4.20.72 + FluentAssertions 8.8.0 + coverlet 6.0.0. **237 tests, all passing.**

## Test Files

| File | # | Coverage |
|------|---|----------|
| `Data/MailAggregatorDbContextTests.cs` | 6 | Models, relations, cascade delete, unique indexes |
| `Auth/CredentialEncryptionServiceTests.cs` | 8 | Encrypt/decrypt, nonce randomness, tamper, key persistence |
| `Auth/PasswordAuthServiceTests.cs` | 15 | Store/retrieve/clear, validation, round-trip |
| `Auth/OAuthServiceTests.cs` | 35 | Host matching, PKCE, token exchange/refresh, invalid_grant, state, encryption |
| `Discovery/AutoDiscoveryServiceTests.cs` | 35 | 6-level fallback, XML, socketType, MX/ccSLD, errors, cancellation |
| `Mail/EmailSyncServiceTests.cs` | 9 | SPECIAL-USE attribute mapping |
| `Mail/ImapConnectionServiceTests.cs` | 4 | Encryption type → SecureSocketOptions |
| `Mail/EmailSendServiceTests.cs` | 29 | Quoting, forward, MIME building, address validation |
| `AccountManagement/AccountServiceTests.cs` | 22 | CRUD, discovery, OAuth, duplicates, deletion cleanup |
| `Sync/SyncManagerTests.cs` | 30 | IDLE lifecycle, backoff+jitter, events, reconnect, shutdown |
| `TwoFactor/TwoFactorCodeServiceTests.cs` | 23 | TOTP (6/8 digits, SHA variants), URI parsing |
| `TwoFactor/TwoFactorAccountServiceTests.cs` | 21 | CRUD, encrypted round-trip, URI import, normalization |

## Patterns

- **DB**: In-memory SQLite (`:memory:` + `OpenConnection()`)
- **HTTP**: `MockHttpMessageHandler` (URL → response dict)
- **Mocking**: Moq `Setup/ReturnsAsync/Verify`
- **Assertions**: FluentAssertions. Jitter: range assertions (`BeGreaterThanOrEqualTo`/`BeLessThanOrEqualTo`). Statistical: 20 iterations → `Distinct().Count() > 1`
- **Async**: `[Fact] async Task` + `ThrowAsync<T>()`
- **DNS isolation**: `TestableAutoDiscoveryService` overrides `ResolveMxDomainAsync`/`TryDiscoverViaSrvAsync`
