# MailAggregator Project Index

> Read this first, then relevant chapter(s) per task.

## Overview

Windows desktop email client, direct IMAP/SMTP, no backend.

| Key      | Value                                                   |
| -------- | ------------------------------------------------------- |
| Stack    | C# / .NET 8 / WPF / ModernWpfUI / MailKit / EF Core SQLite / WebView2 |
| Arch     | MVVM (CommunityToolkit.Mvvm) + DI                       |
| Platform | Win x64 (Core cross-platform, Desktop Windows-only)     |
| Tests    | 237 xUnit                                               |

Providers: Gmail, Microsoft, Yahoo, AOL, Fastmail, any standard IMAP/SMTP.

## Directory Tree

```
/workspace/
├── MailAggregator.sln
├── CLAUDE.md
├── .devcontainer/                                  # Ubuntu 22.04 + CUDA 12.4
├── .github/workflows/build.yml                     # v* tag → build → test → release
├── docs-for-ai/
│   ├── index.md, pitfalls.md, thunderbird-comparison.md
│   └── chapters/ (core-data, auth, mail, desktop, tests, two-factor, workflows)
│
└── src/
    ├── MailAggregator.Core/                        # net8.0, cross-platform
    │   ├── oauth-providers.json                    # 5 OAuth providers
    │   ├── Models/
    │   │   ├── Account.cs                          # IMAP/SMTP/auth/proxy entity
    │   │   ├── AuthType.cs                         # Password / OAuth2
    │   │   ├── ConnectionEncryptionType.cs         # None / Ssl / StartTls
    │   │   ├── EmailAttachment.cs                  # Attachment metadata
    │   │   ├── EmailMessage.cs                     # Email entity
    │   │   ├── MailFolder.cs                       # IMAP folder
    │   │   ├── OAuthProviderConfig.cs / OAuthTokenResult.cs
    │   │   ├── OtpAlgorithm.cs                    # Sha1/Sha256/Sha512
    │   │   ├── ServerConfiguration.cs              # Auto-discovered config
    │   │   ├── SpecialFolderType.cs                # Inbox/Sent/Drafts/Trash/Junk/Archive
    │   │   └── TwoFactorAccount.cs                # 2FA TOTP entity
    │   ├── Data/
    │   │   ├── MailAggregatorDbContext.cs
    │   │   └── DatabaseInitializer.cs
    │   └── Services/
    │       ├── Auth/                               # AES-256-GCM, DPAPI, Password, OAuth PKCE
    │       ├── Discovery/AutoDiscoveryService.cs   # 6-level fallback
    │       ├── Mail/                               # ConnectionHelper, IMAP/SMTP, Pool, Sync, Send
    │       ├── AccountManagement/AccountService.cs # Account CRUD
    │       ├── TwoFactor/                          # TOTP code gen + 2FA account CRUD
    │       └── Sync/SyncManager.cs                 # IMAP IDLE background sync
    │
    ├── MailAggregator.Desktop/                     # net8.0-windows
    │   ├── App.xaml(.cs)                           # DI, Serilog, lifecycle
    │   ├── MainWindow.xaml(.cs)                    # 3-pane + WebView2
    │   ├── Resources/Styles.xaml, app.ico          # styles + embedded app icon
    │   ├── ViewModels/                             # Main, AddAccount, AccountList, Compose, TwoFactor*, NotificationHelper
    │   ├── Views/                                  # AddAccount, AccountList, Compose, TwoFactor, AddTwoFactor
    │   └── Converters/                             # BoolToFontWeight, BoolToVisibility, NullToVisibility, FileSize
    │
    └── MailAggregator.Tests/                       # 237 xUnit tests
        ├── Data/MailAggregatorDbContextTests.cs                        # [6]
        └── Services/
            ├── Auth/{Credential,Password,OAuth}ServiceTests.cs         # [8+15+35]
            ├── Discovery/AutoDiscoveryServiceTests.cs                   # [35]
            ├── Mail/{EmailSync,ImapConnection,EmailSend}ServiceTests.cs # [9+4+29]
            ├── AccountManagement/AccountServiceTests.cs                 # [22]
            ├── TwoFactor/{TwoFactorCodeService,TwoFactorAccountService}Tests.cs # [23+21]
            └── Sync/SyncManagerTests.cs                                 # [30]
```

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  WPF UI Layer (MailAggregator.Desktop)                              │
│  ┌──────────────┐  ┌───────────┐  ┌──────────┐  ┌──────────────┐   │
│  │ MainWindow   │  │ AddAccount│  │ Compose  │  │ TwoFactor    │   │
│  │ + ViewModel  │  │ Window+VM │  │ Window+VM│  │ Window + VM  │   │
│  └──────┬───────┘  └──────┬────┘  └──────┬───┘  └──────┬───────┘   │
├─────────┼─────────────────┼───────────────────┼─────────┤
│  Core Service Layer (MailAggregator.Core)                │
│  ┌──────┴───────┐  ┌──────┴────────┐  ┌──────┴───────┐ │
│  │ SyncManager  │  │ AccountService│  │EmailSendSvc  │ │
│  └──────┬───────┘  └──────┬────────┘  └──────┬───────┘ │
│  ┌──────┴──────────────────┴───────────────────┴──────┐ │
│  │           EmailSyncService (IMAP Sync)             │ │
│  └──────────────────────┬─────────────────────────────┘ │
│  ┌──────────────────────┴─────────────────────────────┐ │
│  │  ImapConnectionPool ← ImapConnectionService        │ │
│  │  SmtpConnectionService / MailConnectionHelper      │ │
│  └──────────────────────┬─────────────────────────────┘ │
│  ┌─────────────┬────────┴────────┬────────────────────┐ │
│  │ OAuthService│AutoDiscoverySvc │PasswordAuthService │ │
│  └──────┬──────┘─────────────────┘────────┬───────────┘ │
│  ┌──────┴─────────────────────────────────┴───────────┐ │
│  │  CredentialEncryptionService + KeyProtector         │ │
│  └────────────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────────────┤
│  Data Layer: MailAggregatorDbContext (EF Core + SQLite)  │
└─────────────────────────────────────────────────────────┘
```

## Chapters

| Chapter    | File                    | When to read                              |
| ---------- | ----------------------- | ----------------------------------------- |
| Core Data  | `chapters/core-data.md` | Models, DB schema, EF Core                |
| Auth       | `chapters/auth.md`      | OAuth, encryption, credentials            |
| Mail       | `chapters/mail.md`      | Connection, sync, send, discovery, acct   |
| Desktop    | `chapters/desktop.md`   | UI, ViewModels, views, styles             |
| Tests      | `chapters/tests.md`     | Adding/modifying tests                    |
| Two-Factor | `chapters/two-factor.md`| 2FA TOTP authenticator                    |
| Workflows  | `chapters/workflows.md` | End-to-end flow diagrams                  |
