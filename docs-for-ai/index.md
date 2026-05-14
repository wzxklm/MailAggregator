# MailAggregator Project Index

> Read this first, then relevant chapter(s) per task.

## Overview

WPF desktop email client aggregating multiple IMAP accounts with OAuth2/password auth, background IDLE sync, and a built-in TOTP authenticator.

| Key      | Value |
|----------|-------|
| Stack    | C# / .NET 8 / WPF / MailKit / EF Core SQLite / Serilog / CommunityToolkit.Mvvm |
| Arch     | MVVM + Dependency Injection (Microsoft.Extensions.DependencyInjection) |
| Platform | Windows x64 (self-contained) |
| Tests    | xUnit 2.5.3 + Moq + FluentAssertions — 237 tests in `src/MailAggregator.Tests/` |
| Build    | `dotnet build MailAggregator.sln` |
| Test Cmd | `dotnet test src/MailAggregator.Tests/MailAggregator.Tests.csproj` |

## Directory Tree

```
/
├── MailAggregator.sln
├── .github/workflows/build.yml          # Tag-triggered CI/CD (v* tags only)
├── src/
│   ├── MailAggregator.Core/             # Cross-platform core library (net8.0)
│   │   ├── Data/                        # EF Core DbContext, DatabaseInitializer
│   │   ├── Models/                      # Account, EmailMessage, MailFolder, TwoFactorAccount, enums
│   │   ├── Services/
│   │   │   ├── AccountManagement/       # Account CRUD (AccountService)
│   │   │   ├── Auth/                    # Encryption, password, OAuth, key protectors
│   │   │   ├── Discovery/              # IMAP/SMTP auto-discovery (6-level fallback)
│   │   │   ├── Mail/                   # IMAP/SMTP connection, pooling, sync, send, message ops
│   │   │   ├── Sync/                   # SyncManager — background IDLE/polling orchestration
│   │   │   └── TwoFactor/             # TOTP code generation & account management
│   │   └── oauth-providers.json        # OAuth provider configuration (Google, Microsoft, etc.)
│   │
│   ├── MailAggregator.Desktop/          # WPF UI layer (net8.0-windows)
│   │   ├── App.xaml(.cs)               # Entry point, DI container setup
│   │   ├── MainWindow.xaml(.cs)        # Main window: email list, folder tree, WebView2 preview
│   │   ├── ViewModels/                 # MVVM ViewModels (MainVM split into partials, AccountListVM, ComposeVM, 2FA VMs)
│   │   ├── Views/                      # Dialog windows (AddAccount, Compose, TwoFactor, etc.)
│   │   ├── Converters/                 # XAML value converters
│   │   └── Resources/Styles.xaml       # Global WPF styles
│   │
│   └── MailAggregator.Tests/           # xUnit test suite (net8.0)
│       ├── Data/                       # DbContext tests
│       └── Services/                   # Service tests (mirrors Core/Services structure)
```

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Desktop (WPF UI)                      │
│  MainWindow ← MainViewModel ← AccountList/Compose/2FA   │
│       │              │                                   │
│       ▼              ▼                                   │
│   WebView2    Dispatcher marshalling                     │
└──────┬───────────────┬───────────────────────────────────┘
       │               │
       ▼               ▼
┌─────────────────────────────────────────────────────────┐
│                   Core Services                          │
│                                                          │
│  AccountService ──→ AutoDiscoveryService                 │
│       │                                                  │
│       ├──→ PasswordAuthService ──→ CredentialEncryption  │
│       ├──→ OAuthService ─────────→ CredentialEncryption  │
│       │                                   │              │
│       ▼                                   ▼              │
│  SyncManager ──→ ImapConnectionService ← MailConnHelper  │
│       │              │                       │           │
│       │              ▼                       ▼           │
│       │         ImapConnectionPool    SmtpConnectionSvc  │
│       │              │                       │           │
│       ▼              ▼                       ▼           │
│  EmailSyncService ──→ ImapFolderDiscovery                │
│  EmailOperationService               EmailSendService    │
│                                                          │
│  TwoFactorAccountSvc ──→ TwoFactorCodeSvc               │
│         │                                                │
│         ▼                                                │
│  CredentialEncryptionService ──→ IKeyProtector           │
│                                  (DPAPI / DevProtector)  │
└──────────────────────┬───────────────────────────────────┘
                       │
                       ▼
┌─────────────────────────────────────────────────────────┐
│              Data Layer (EF Core + SQLite)                │
│  MailAggregatorDbContext ← DatabaseInitializer            │
│  Models: Account, MailFolder, EmailMessage, Attachment,   │
│          TwoFactorAccount, ServerConfiguration            │
└─────────────────────────────────────────────────────────┘
```

## Dependency Impact Map

| Component | Affects |
|-----------|---------|
| `CredentialEncryptionService` | PasswordAuthService, OAuthService, MailConnectionHelper, TwoFactorAccountService, ImapConnectionService, SmtpConnectionService |
| `MailAggregatorDbContext` | AccountService, EmailSyncService, EmailOperationService, EmailSendService, ImapConnectionService, SmtpConnectionService, TwoFactorAccountService, MainViewModel |
| `MailConnectionHelper` | ImapConnectionService, SmtpConnectionService, SyncManager (shared retry/auth/token-refresh logic) |
| `ImapConnectionService` | ImapConnectionPool, SyncManager, EmailSendService (Sent folder append) |
| `ImapConnectionPool` | EmailSyncService, EmailOperationService, AccountService (cleanup on delete) |
| `Account` model | All services and ViewModels — central entity |

## Chapters

| Chapter | File | When to read |
|---------|------|--------------|
| Core Data | `chapters/core-data.md` | Models, DbContext, schema, timestamps, migrations |
| Auth | `chapters/auth/` | Encryption, passwords, OAuth, key protectors |
| Account Management | `chapters/account-management.md` | Account CRUD, add/edit/delete flows |
| Discovery | `chapters/discovery.md` | IMAP/SMTP auto-discovery, DNS, autoconfig XML |
| Mail | `chapters/mail/` | IMAP/SMTP connections, pooling, sync, send |
| Sync | `chapters/sync.md` | Background sync, IDLE, polling, reconnection |
| Two-Factor | `chapters/two-factor.md` | TOTP codes, 2FA account management |
| Desktop | `chapters/desktop/` | WPF UI, ViewModels, views, converters, DI setup |
| Tests | `chapters/tests.md` | Test framework, patterns, mocking, running tests |
| Workflows | `chapters/workflows.md` | Any cross-component task |
