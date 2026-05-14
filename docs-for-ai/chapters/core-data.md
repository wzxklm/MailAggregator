# Core Data — EF Core database layer, models, and initialization

## Files

| File | Responsibility |
|------|----------------|
| `src/MailAggregator.Core/Data/MailAggregatorDbContext.cs` | EF Core DbContext — DbSets, entity config, auto-timestamps, SQLite DateTimeOffset conversion |
| `src/MailAggregator.Core/Data/DatabaseInitializer.cs` | Startup DB creation + schema migration for SQLite |
| `src/MailAggregator.Core/Models/Account.cs` | Mail account entity (IMAP/SMTP/auth/proxy settings) |
| `src/MailAggregator.Core/Models/MailFolder.cs` | IMAP folder entity with UID tracking |
| `src/MailAggregator.Core/Models/EmailMessage.cs` | Cached email entity (headers, body, status) |
| `src/MailAggregator.Core/Models/EmailAttachment.cs` | Attachment metadata entity |
| `src/MailAggregator.Core/Models/TwoFactorAccount.cs` | TOTP 2FA account entity (encrypted secret) |
| `src/MailAggregator.Core/Models/AuthType.cs` | Enum: `Password`, `OAuth2` |
| `src/MailAggregator.Core/Models/ConnectionEncryptionType.cs` | Enum: `None`, `Ssl`, `StartTls` |
| `src/MailAggregator.Core/Models/SpecialFolderType.cs` | Enum: `None`, `Inbox`, `Sent`, `Drafts`, `Trash`, `Junk`, `Archive` |
| `src/MailAggregator.Core/Models/OtpAlgorithm.cs` | Enum: `Sha1`, `Sha256`, `Sha512` |
| `src/MailAggregator.Core/Models/ServerConfiguration.cs` | DTO — auto-discovered IMAP/SMTP server settings |
| `src/MailAggregator.Core/Models/OAuthProviderConfig.cs` | DTO — OAuth provider config loaded from `oauth-providers.json` |
| `src/MailAggregator.Core/Models/OAuthTokenResult.cs` | DTO — OAuth token exchange result |

## MailAggregatorDbContext

### Overview

EF Core `DbContext` for SQLite. Provides five `DbSet` properties covering all persisted entities. Handles automatic timestamp stamping on save and global `DateTimeOffset` → UTC ticks conversion for SQLite compatibility.

### Key Behaviors

- **Auto-timestamps**: Overrides `SaveChanges`/`SaveChangesAsync` to stamp `CreatedAt`/`UpdatedAt` on `Account` and `TwoFactorAccount` entities (Added/Modified), and `CachedAt` on `EmailMessage` (Added only)
- **DateTimeOffset → long conversion**: Global `ValueConverter` stores all `DateTimeOffset` properties as UTC ticks (`long`). Required because SQLite cannot natively ORDER BY or compare `DateTimeOffset` values
- **Unique constraints**: `Account.EmailAddress`, `(MailFolder.AccountId, MailFolder.FullName)`, `(EmailMessage.FolderId, EmailMessage.Uid)`
- **Cascade deletes**: Account deletion cascades to folders and messages; folder deletion cascades to messages; message deletion cascades to attachments
- **Indexes**: `EmailMessage` has indexes on `AccountId`, `DateSent`, and composite `(FolderId, DateSent)` for query performance

### Interface

`MailAggregatorDbContext(DbContextOptions<MailAggregatorDbContext>)` — constructor injection, typically via `IDbContextFactory<MailAggregatorDbContext>`

DbSets: `Accounts`, `Folders`, `Messages`, `Attachments`, `TwoFactorAccounts`

### Internal Details

Entity configuration is in `OnModelCreating` (fluent API). Key column constraints:

| Entity | Column | Constraint |
|--------|--------|------------|
| `Account` | `EmailAddress` | Required, max 256, unique index |
| `Account` | `ImapHost`, `SmtpHost` | Required, max 256 |
| `MailFolder` | `FullName` | Required, max 512, unique per account |
| `EmailMessage` | `Uid` | Unique per folder |
| `EmailMessage` | `Subject` | Max 1024 |
| `TwoFactorAccount` | `EncryptedSecret` | Required (no max) |
| `EmailAttachment` | `FileName` | Required, max 512 |

### Dependencies

- Uses: EF Core, `Microsoft.EntityFrameworkCore`, all model classes
- Used by: `AccountService`, `EmailSyncService` (via `IDbContextFactory`), `EmailSendService`, `SmtpConnectionService`, `ImapConnectionService`, `TwoFactorAccountService`, `MainViewModel`

---

## DatabaseInitializer

### Overview

Static helper that ensures the SQLite database and all tables exist at app startup. Handles incremental schema migrations that `EnsureCreatedAsync` cannot perform on existing databases.

### Key Behaviors

- **Initial creation**: Calls `EnsureCreatedAsync()` to create DB + tables on first run
- **Manual table migration**: Executes `CREATE TABLE IF NOT EXISTS TwoFactorAccounts` for existing databases where `EnsureCreatedAsync` is a no-op (it only creates tables when the DB file is new)
- **Column migration**: Adds `UseIdle` column to `Accounts` via `ALTER TABLE ADD COLUMN`. Catches `SqliteException` (error code 1) when column already exists

### Interface

`DatabaseInitializer.InitializeAsync(MailAggregatorDbContext)` — static async method, called once at startup

### Internal Details

SQLite lacks `ALTER TABLE ADD COLUMN IF NOT EXISTS`, so the column migration uses try/catch on `SqliteException` with `SqliteErrorCode == 1` (SQLITE_ERROR for duplicate column). Each new column migration follows this pattern.

### Dependencies

- Uses: `MailAggregatorDbContext`, `Microsoft.Data.Sqlite`, Serilog
- Used by: `App.xaml.cs` (startup)

---

## Internal Types

### Entity Models (persisted)

| Model | PK Type | Key Properties | Relationships |
|-------|---------|----------------|---------------|
| `Account` | `int` | `EmailAddress`, `ImapHost`/`ImapPort`, `SmtpHost`/`SmtpPort`, `AuthType`, `EncryptedPassword`, `EncryptedAccessToken`/`EncryptedRefreshToken`, `ProxyHost`/`ProxyPort`, `IsEnabled`, `UseIdle` | Has many `MailFolder`, has many `EmailMessage` |
| `MailFolder` | `int` | `Name`, `FullName`, `SpecialUse`, `UidValidity`, `MaxUid`, `MessageCount`, `UnreadCount` | Belongs to `Account`, has many `EmailMessage` |
| `EmailMessage` | `long` | `Uid`, `MessageId`, `InReplyTo`, `References`, `FromAddress`/`FromName`, `ToAddresses`, `CcAddresses`, `BccAddresses`, `Subject`, `DateSent`, `PreviewText`, `BodyText`, `BodyHtml`, `IsRead`, `IsFlagged`, `HasAttachments`, `CachedAt` | Belongs to `Account`, belongs to `MailFolder`, has many `EmailAttachment` |
| `EmailAttachment` | `long` | `FileName`, `ContentType`, `Size`, `LocalPath`, `ContentId` | Belongs to `EmailMessage` |
| `TwoFactorAccount` | `int` | `Issuer`, `Label`, `EncryptedSecret`, `Algorithm`, `Digits` (default 6), `Period` (default 30) | None |

### Enums

| Enum | Values |
|------|--------|
| `AuthType` | `Password`, `OAuth2` |
| `ConnectionEncryptionType` | `None`, `Ssl`, `StartTls` |
| `SpecialFolderType` | `None`, `Inbox`, `Sent`, `Drafts`, `Trash`, `Junk`, `Archive` |
| `OtpAlgorithm` | `Sha1`, `Sha256`, `Sha512` |

### DTOs (not persisted)

| Model | Purpose | Key Properties |
|-------|---------|----------------|
| `ServerConfiguration` | AutoDiscovery result — discovered IMAP/SMTP settings | `ImapHost`, `ImapPort`, `ImapEncryption`, `SmtpHost`, `SmtpPort`, `SmtpEncryption`, `Authentication` |
| `OAuthProviderConfig` | OAuth provider definition from `oauth-providers.json` | `Name`, `ServerHosts` (hostname match list), `ClientId`, `ClientSecret`, `AuthorizationEndpoint`, `TokenEndpoint`, `Scopes`, `UsePKCE`, `RedirectionEndpoint`, `AdditionalAuthParams` |
| `OAuthTokenResult` | Token exchange response | `AccessToken`, `RefreshToken`, `ExpiresAt`, `GrantedScopes` |
