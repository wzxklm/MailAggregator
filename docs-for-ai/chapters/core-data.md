# Core Data — Models & Data Access

## Project Config

**`MailAggregator.Core.csproj`**: `net8.0`, `InternalsVisibleTo: MailAggregator.Tests`
- NuGet: MailKit 4.15.1, EF Core SQLite 8.x, Otp.NET 1.4.0, DnsClient 1.8.0, Serilog 4.3.1, ProtectedData 10.0.3

**`oauth-providers.json`**: 5 providers (Gmail, Microsoft, Yahoo, AOL, Fastmail)
- Per provider: `name`, `serverHosts[]`, `clientId`, `clientSecret`?, `authorizationEndpoint`, `tokenEndpoint`, `redirectionEndpoint`?, `scopes[]`, `usePKCE`, `additionalAuthParams`?

---

## Data Models — `Models/`

### `Account.cs`
- **Connection**: `ImapHost/Port/Encryption`, `SmtpHost/Port/Encryption`
- **Auth**: `AuthType`, `EncryptedPassword`, `EncryptedAccessToken`, `EncryptedRefreshToken`, `OAuthTokenExpiry`
- **Proxy**: `ProxyHost`, `ProxyPort` (SOCKS5)
- **Meta**: `IsEnabled`, `CreatedAt`, `UpdatedAt`
- **Nav**: `Folders` (1:N), `Messages` (1:N)

### `MailFolder.cs`
`AccountId`, `Name`, `FullName`, `SpecialUse`, `UidValidity`, `MaxUid`, `MessageCount`, `UnreadCount`

### `EmailMessage.cs`
- **Headers**: `MessageId`, `InReplyTo`, `References`
- **Addresses**: `FromAddress`, `FromName`, `ToAddresses`, `CcAddresses`, `BccAddresses`
- **Content**: `Subject`, `PreviewText`, `BodyText`, `BodyHtml`
- **Status**: `IsRead`, `HasAttachments`, `Uid`
- **Nav**: `Attachments` (1:N)

### `EmailAttachment.cs`
`FileName`, `ContentType`, `Size`, `LocalPath`, `ContentId`

### `ServerConfiguration.cs`
IMAP/SMTP `Host`, `Port`, `Encryption`

### `OAuthProviderConfig.cs`
`Name`, `ServerHosts[]`, `ClientId`, `ClientSecret`, `AuthorizationEndpoint`, `TokenEndpoint`, `Scopes[]`, `UsePKCE`, `RedirectionEndpoint`, `AdditionalAuthParams`

### `OAuthTokenResult.cs`
`AccessToken`, `RefreshToken`, `ExpiresAt`

### Enums
- `AuthType` — `Password` / `OAuth2`
- `ConnectionEncryptionType` — `None` / `Ssl` / `StartTls`
- `OtpAlgorithm` — `Sha1` (default) / `Sha256` / `Sha512`
- `SpecialFolderType` — `None` / `Inbox` / `Sent` / `Drafts` / `Trash` / `Junk` / `Archive`

### `TwoFactorAccount.cs`
- `Id` (PK), `Issuer` (required, max 256), `Label` (required, max 256)
- `EncryptedSecret` — encrypted via `ICredentialEncryptionService`
- `Algorithm` (default: Sha1), `Digits` (default: 6), `Period` (default: 30s)
- `CreatedAt`, `UpdatedAt` — auto-stamped by `StampTimestamps()`
- **Standalone**: No FK to `Account`

---

## Data Access — `Data/`

### `MailAggregatorDbContext.cs`
- **DbSets**: `Accounts`, `Folders`, `Messages`, `Attachments`, `TwoFactorAccounts` (expression-bodied `Set<TwoFactorAccount>()`)
- **Type conversion**: `DateTimeOffsetToLongConverter` → UTC ticks (SQLite ORDER BY/WHERE compat)
- **`StampTimestamps()`**: Insert → `CreatedAt`+`UpdatedAt` (Account, TwoFactorAccount), `CachedAt` (EmailMessage). Modify → `UpdatedAt`
- **Cascade delete**: Account → Folder → Message → Attachment
- **Unique indexes**: `EmailAddress` (Account), `(AccountId, FullName)` (MailFolder), `(FolderId, Uid)` (EmailMessage)
- **TwoFactorAccount config**: PK `Id`, required `Issuer`/`Label` (max 256), required `EncryptedSecret`

### `DatabaseInitializer.cs`
- `InitializeAsync()`: `EnsureCreatedAsync()` + explicit `CREATE TABLE IF NOT EXISTS TwoFactorAccounts` for existing DBs
- Schema: `Id` INTEGER PK AUTOINCREMENT, `Issuer`/`Label`/`EncryptedSecret` TEXT NOT NULL, `Algorithm` INTEGER DEFAULT 0, `Digits` INTEGER DEFAULT 6, `Period` INTEGER DEFAULT 30, `CreatedAt`/`UpdatedAt` INTEGER DEFAULT 0
