# Tests

## Config

**File**: `src/MailAggregator.Tests/MailAggregator.Tests.csproj`
- xUnit 2.5.3 + Moq 4.20.72 + FluentAssertions 8.8.0
- coverlet.collector 6.0.0
- 180 tests, all passing

## Test Files

| Test File | Count | Target |
|-----------|-------|--------|
| `Data/MailAggregatorDbContextTests.cs` | 6 | EF Core models, relations, cascade delete, unique indexes |
| `Services/Auth/CredentialEncryptionServiceTests.cs` | 8 | AES-256-GCM encrypt/decrypt, nonce randomness, tamper detection |
| `Services/Auth/PasswordAuthServiceTests.cs` | 15 | Password store/retrieve/clear, input validation |
| `Services/Auth/OAuthServiceTests.cs` | 33 | PKCE flow, provider matching, token exchange/refresh, state parameter uniqueness |
| `Services/Discovery/AutoDiscoveryServiceTests.cs` | 35 | 5-level fallback, XML parsing, MX extraction, ccSLD handling, error recovery |
| `Services/Mail/EmailSyncServiceTests.cs` | 9 | SPECIAL-USE attribute mapping |
| `Services/Mail/ImapConnectionServiceTests.cs` | 4 | Encryption type mapping (SecureSocketOptions) |
| `Services/Mail/EmailSendServiceTests.cs` | 18 | Quote reply, forward body, attachment building, MimeMessage |
| `Services/AccountManagement/AccountServiceTests.cs` | 20 | Account CRUD, auto-discovery, OAuth detection, duplicate detection, deletion cleanup |
| `Services/Sync/SyncManagerTests.cs` | 30 | IDLE lifecycle, exponential backoff with jitter, events, graceful shutdown |

## Test Patterns

- **DB isolation**: In-memory SQLite (`DataSource=:memory:`) + `OpenConnection()` to keep session
- **HTTP mock**: Custom `MockHttpMessageHandler`, URL → response dictionary
- **Moq**: `Mock<T>` + `.Setup()` / `.ReturnsAsync()` / `.Verify(Times.Once)`
- **FluentAssertions**: `.Should().Be()` / `.Should().Throw<T>()` / `.Should().HaveCount()`
- **Async tests**: `[Fact] public async Task` + `await act.Should().ThrowAsync<T>()`
- **Range assertions for jittered values**: Backoff tests use `.BeGreaterThanOrEqualTo()` / `.BeLessThanOrEqualTo()` to verify values fall within expected jitter range (e.g., base +/-25%)
- **Statistical verification**: Jitter test runs 20 iterations and asserts `.Distinct().Count().Should().BeGreaterThan(1)` to verify randomization
- **Virtual method override for test isolation**: `AutoDiscoveryServiceTests` uses `TestableAutoDiscoveryService` subclass that overrides `ResolveMxDomainAsync` and `TryDiscoverViaSrvAsync` to avoid real DNS lookups
