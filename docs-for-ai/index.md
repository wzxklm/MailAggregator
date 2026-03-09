# MailAggregator Project Index

> AI reads this file first, then reads only the relevant chapters for the current task.

## Overview

MailAggregator — Windows desktop email aggregation client, direct IMAP/SMTP, no backend.

| Key          | Value                                                   |
| ------------ | ------------------------------------------------------- |
| Stack        | C# / .NET 8 / WPF / MailKit / EF Core SQLite / WebView2 |
| Architecture | MVVM (CommunityToolkit.Mvvm) + DI                       |
| Platform     | Windows x64 (Core cross-platform, Desktop Windows-only) |
| Version      | v1.0.8                                                  |
| Tests        | 179 xUnit tests                                         |

Supported providers: Gmail, Microsoft, Yahoo, AOL, Fastmail, any standard IMAP/SMTP server.

## Directory Tree

```
/workspace/
│
├── MailAggregator.sln                              # Solution (Core, Desktop, Tests)
├── CLAUDE.md                                       # AI instructions
├── .mcp.json                                       # MCP tool config
│
├── .devcontainer/
│   ├── Dockerfile                                  # Ubuntu 22.04 + CUDA 12.4
│   ├── devcontainer.json                           # VSCode extensions & settings
│   └── docker-compose.yml                          # Docker Compose (GPU, volumes)
│
├── .github/workflows/
│   └── build.yml                                   # CI/CD: v* tag → build → test → release
│
├── docs-for-ai/
│   ├── index.md                                    # This file (project index)
│   ├── pitfalls.md                                 # Pitfalls & conventions
│   ├── thunderbird-comparison.md                   # Security audit
│   └── chapters/                                   # Detailed documentation chapters
│       ├── core-data.md                            # Models + Data layer
│       ├── auth.md                                 # Auth services + Security
│       ├── mail.md                                 # Discovery + Mail + Sync + Account
│       ├── desktop.md                              # WPF UI layer
│       ├── tests.md                                # Test layer
│       └── workflows.md                            # Core workflow diagrams
│
└── src/
    │
    ├── MailAggregator.Core/                        # ═══ Core (net8.0, cross-platform) ═══
    │   ├── MailAggregator.Core.csproj
    │   ├── oauth-providers.json                    # OAuth provider configs (5 providers)
    │   ├── oauth-providers.local.json              # Local overrides (not in VCS)
    │   ├── Models/
    │   │   ├── Account.cs                          # Account entity (IMAP/SMTP/auth/proxy)
    │   │   ├── AuthType.cs                         # Password / OAuth2
    │   │   ├── ConnectionEncryptionType.cs         # None / Ssl / StartTls
    │   │   ├── EmailAttachment.cs                  # Attachment metadata
    │   │   ├── EmailMessage.cs                     # Email entity (headers/body/status/UID)
    │   │   ├── MailFolder.cs                       # IMAP folder (SpecialUse/UidValidity)
    │   │   ├── OAuthProviderConfig.cs              # OAuth provider config model
    │   │   ├── OAuthTokenResult.cs                 # Token response
    │   │   ├── ServerConfiguration.cs              # Auto-discovered server config
    │   │   └── SpecialFolderType.cs                # Inbox/Sent/Drafts/Trash/Junk/Archive
    │   ├── Data/
    │   │   ├── MailAggregatorDbContext.cs           # EF Core DbContext (SQLite)
    │   │   └── DatabaseInitializer.cs              # EnsureCreatedAsync
    │   └── Services/
    │       ├── Auth/
    │       │   ├── ICredentialEncryptionService.cs / CredentialEncryptionService.cs   # AES-256-GCM
    │       │   ├── IKeyProtector.cs / DpapiKeyProtector.cs / DevKeyProtector.cs       # Key protection
    │       │   ├── IPasswordAuthService.cs / PasswordAuthService.cs                   # Password auth
    │       │   ├── IOAuthService.cs / OAuthService.cs                                 # OAuth 2.0 PKCE
    │       │   └── OAuthReauthenticationRequiredException.cs                          # invalid_grant exception
    │       ├── Discovery/
    │       │   └── IAutoDiscoveryService.cs / AutoDiscoveryService.cs                 # 5-level fallback
    │       ├── Mail/
    │       │   ├── MailConnectionHelper.cs          # Shared auth/proxy/encryption logic
    │       │   ├── IImapConnectionService.cs / ImapConnectionService.cs               # IMAP factory
    │       │   ├── ISmtpConnectionService.cs / SmtpConnectionService.cs               # SMTP factory
    │       │   ├── IImapConnectionPool.cs / ImapConnectionPool.cs / PooledImapConnection.cs  # Connection pool
    │       │   ├── IEmailSyncService.cs / EmailSyncService.cs                         # Folder/email sync
    │       │   └── IEmailSendService.cs / EmailSendService.cs                         # Send/reply/forward
    │       ├── AccountManagement/
    │       │   └── IAccountService.cs / AccountService.cs                             # Account CRUD
    │       └── Sync/
    │           └── ISyncManager.cs / SyncManager.cs                                   # IMAP IDLE background sync
    │
    ├── MailAggregator.Desktop/                     # ═══ WPF UI (net8.0-windows) ═══
    │   ├── MailAggregator.Desktop.csproj
    │   ├── App.xaml / App.xaml.cs                  # DI container, Serilog, lifecycle
    │   ├── MainWindow.xaml / MainWindow.xaml.cs    # 3-pane layout + WebView2
    │   ├── Resources/Styles.xaml                   # App-level styles & converters
    │   ├── ViewModels/
    │   │   ├── MainViewModel.cs                    # Folder tree, email list, sync
    │   │   ├── AddAccountViewModel.cs              # 5-step wizard + OAuth flow
    │   │   ├── AccountListViewModel.cs             # Account CRUD UI
    │   │   ├── ComposeViewModel.cs                 # New/Reply/Forward
    │   │   └── NotificationHelper.cs               # Toast notifications
    │   ├── Views/
    │   │   ├── AddAccountWindow.xaml/.cs            # Account wizard
    │   │   ├── AccountListWindow.xaml/.cs           # Account management
    │   │   └── ComposeWindow.xaml/.cs               # Compose email
    │   └── Converters/
    │       ├── BoolToFontWeightConverter.cs         # !IsRead → Bold
    │       ├── BoolToVisibilityConverter.cs         # Bool → Visible/Collapsed
    │       ├── NullToVisibilityConverter.cs         # Null → Collapsed
    │       └── FileSizeConverter.cs                 # Bytes → "1.5 MB"
    │
    └── MailAggregator.Tests/                       # ═══ Tests (net8.0, 179 tests) ═══
        ├── MailAggregator.Tests.csproj
        ├── Data/MailAggregatorDbContextTests.cs                        # [6]
        └── Services/
            ├── Auth/{Credential,Password,OAuth}ServiceTests.cs         # [8+15+33]
            ├── Discovery/AutoDiscoveryServiceTests.cs                   # [35]
            ├── Mail/{EmailSync,ImapConnection,EmailSend}ServiceTests.cs # [9+4+18]
            ├── AccountManagement/AccountServiceTests.cs                 # [20]
            └── Sync/SyncManagerTests.cs                                 # [29]
```

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│  WPF UI Layer (MailAggregator.Desktop)                  │
│  ┌──────────────┐  ┌───────────────┐  ┌──────────────┐ │
│  │ MainWindow   │  │ AddAccount    │  │ Compose      │ │
│  │ + ViewModel  │  │ Window + VM   │  │ Window + VM  │ │
│  └──────┬───────┘  └──────┬────────┘  └──────┬───────┘ │
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

| Chapter         | File                    | When to read                                                 |
| --------------- | ----------------------- | ------------------------------------------------------------ |
| Core Data       | `chapters/core-data.md` | Modifying models, DB schema, EF Core config                  |
| Auth & Security | `chapters/auth.md`      | Auth bugs, OAuth, encryption, credential storage             |
| Mail Services   | `chapters/mail.md`      | Connection, sync, send, discovery, account mgmt, concurrency |
| Desktop UI      | `chapters/desktop.md`   | UI changes, ViewModels, views, styles                        |
| Tests           | `chapters/tests.md`     | Adding/modifying tests                                       |
| Workflows       | `chapters/workflows.md` | Understanding end-to-end flows                               |
