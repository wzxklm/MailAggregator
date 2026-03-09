# Core Data — Models & Data Access Layer

## Project Config

**File**: `src/MailAggregator.Core/MailAggregator.Core.csproj`
- TargetFramework: `net8.0` (cross-platform)
- Key NuGet:
  - **MailKit 4.15.1** — IMAP/SMTP
  - **Microsoft.EntityFrameworkCore.Sqlite 8.x** — SQLite ORM
  - **Serilog 4.3.1** — Structured logging
  - **System.Security.Cryptography.ProtectedData 10.0.3** — DPAPI
- `InternalsVisibleTo: MailAggregator.Tests`
- `oauth-providers.json` copied to output

**File**: `src/MailAggregator.Core/oauth-providers.json`
- 5 OAuth providers: Gmail, Microsoft, Yahoo, AOL, Fastmail
- Each: `name`, `serverHosts[]`, `clientId`, `clientSecret`(optional), `authorizationEndpoint`, `tokenEndpoint`, `redirectionEndpoint`(optional), `scopes[]`, `usePKCE`, `additionalAuthParams`(optional)

---

## Data Models — `Models/`

### `Account.cs` — Email account entity
- **Connection**: `ImapHost/Port/Encryption`, `SmtpHost/Port/Encryption`
- **Auth**: `AuthType` (Password/OAuth2), `EncryptedPassword`, `EncryptedAccessToken`, `EncryptedRefreshToken`, `OAuthTokenExpiry`
- **Proxy**: `ProxyHost`, `ProxyPort` (SOCKS5)
- **Status**: `IsEnabled`, `CreatedAt`, `UpdatedAt`
- **Navigation**: `Folders` (1:N), `Messages` (1:N)

### `MailFolder.cs` — IMAP folder entity
- `AccountId`, `Name`, `FullName`
- `SpecialUse` (Inbox/Sent/Drafts/Trash/Junk/Archive/None)
- `UidValidity`, `MaxUid` (incremental sync markers)
- `MessageCount`, `UnreadCount`

### `EmailMessage.cs` — Email entity
- **Headers**: `MessageId`, `InReplyTo`, `References` (threading)
- **Addresses**: `FromAddress`, `FromName`, `ToAddresses`, `CcAddresses`, `BccAddresses`
- **Content**: `Subject`, `PreviewText`, `BodyText`, `BodyHtml`
- **Status**: `IsRead`, `HasAttachments`, `Uid` (IMAP UID)
- **Navigation**: `Attachments` (1:N)

### `EmailAttachment.cs` — Attachment metadata
- `FileName`, `ContentType`, `Size`
- `LocalPath` (local cache), `ContentId` (inline images)

### `ServerConfiguration.cs` — Auto-discovered server config
- IMAP/SMTP `Host`, `Port`, `Encryption`

### `OAuthProviderConfig.cs` — OAuth provider config
- `Name`, `ServerHosts[]`, `ClientId`, `ClientSecret`, `AuthorizationEndpoint`, `TokenEndpoint`
- `Scopes[]`, `UsePKCE` (per-provider), `RedirectionEndpoint`, `AdditionalAuthParams`

### `OAuthTokenResult.cs` — Token response
- `AccessToken`, `RefreshToken`, `ExpiresAt`

### Enums
- `AuthType.cs` — `Password` / `OAuth2`
- `ConnectionEncryptionType.cs` — `None` / `Ssl` / `StartTls`
- `SpecialFolderType.cs` — `None` / `Inbox` / `Sent` / `Drafts` / `Trash` / `Junk` / `Archive`

---

## Data Access — `Data/`

### `MailAggregatorDbContext.cs` — EF Core DbContext
- **DbSet**: `Accounts`, `Folders`, `Messages`, `Attachments`
- **Type conversion**: Custom `DateTimeOffsetToLongConverter` → UTC ticks (SQLite doesn't support DateTimeOffset ORDER BY/WHERE)
- **Auto timestamps**: `CreatedAt`/`UpdatedAt` (Account), `CachedAt` (EmailMessage)
- **Cascade delete**: Account → Folder → Message → Attachment
- **Unique indexes**: `(AccountId, FullName)` for MailFolder, `(FolderId, Uid)` for EmailMessage
- **Key methods**: `OnModelCreating()`, `SaveChangesAsync()` (auto timestamps)

### `DatabaseInitializer.cs`
- `InitializeAsync()` — calls `EnsureCreatedAsync()`
