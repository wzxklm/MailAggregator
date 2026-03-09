# Tests

## Config

**File**: `src/MailAggregator.Tests/MailAggregator.Tests.csproj`
- xUnit 2.5.3 + Moq 4.20.72 + FluentAssertions 8.8.0
- coverlet.collector 6.0.0
- 163 tests, all passing

## Test Files

| Test File | Count | Target |
|-----------|-------|--------|
| `Data/MailAggregatorDbContextTests.cs` | 13 | EF Core models, relations, cascade delete, unique indexes |
| `Services/Auth/CredentialEncryptionServiceTests.cs` | 8 | AES-256-GCM encrypt/decrypt, nonce randomness, tamper detection |
| `Services/Auth/PasswordAuthServiceTests.cs` | 14 | Password store/retrieve/clear, input validation |
| `Services/Auth/OAuthServiceTests.cs` | 30 | PKCE flow, provider matching, token exchange/refresh |
| `Services/Discovery/AutoDiscoveryServiceTests.cs` | 26 | 5-level fallback, XML parsing, MX extraction, error recovery |
| `Services/Mail/EmailSyncServiceTests.cs` | 8 | SPECIAL-USE attribute mapping |
| `Services/Mail/ImapConnectionServiceTests.cs` | 2 | Encryption type mapping (SecureSocketOptions) |
| `Services/Mail/EmailSendServiceTests.cs` | 20 | Quote reply, forward body, attachment building, MimeMessage |
| `Services/AccountManagement/AccountServiceTests.cs` | 20 | Account CRUD, auto-discovery, OAuth detection, connection validation |
| `Services/Sync/SyncManagerTests.cs` | 33 | IDLE lifecycle, exponential backoff, events, graceful shutdown |

## Test Patterns

- **DB isolation**: In-memory SQLite (`DataSource=:memory:`) + `OpenConnection()` to keep session
- **HTTP mock**: Custom `MockHttpMessageHandler`, URL → response dictionary
- **Moq**: `Mock<T>` + `.Setup()` / `.ReturnsAsync()` / `.Verify(Times.Once)`
- **FluentAssertions**: `.Should().Be()` / `.Should().Throw<T>()` / `.Should().HaveCount()`
- **Async tests**: `[Fact] public async Task` + `await act.Should().ThrowAsync<T>()`
