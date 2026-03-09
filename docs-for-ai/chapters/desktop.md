# Desktop UI — WPF Layer

## Project Config

**File**: `src/MailAggregator.Desktop/MailAggregator.Desktop.csproj`
- TargetFramework: `net8.0-windows` (Windows only)
- OutputType: WinExe
- UseWPF: true, UseWindowsForms: true (NotifyIcon for Toast)
- Key deps: CommunityToolkit.Mvvm 8.4.0, WebView2 1.0.3800.47, DI 8.0.1

---

## App Entry — `App.xaml` / `App.xaml.cs`

**DI container core.** Configures all service dependency injection.

**Startup flow** (`OnStartup`):
1. Create app directory: `%AppData%\MailAggregator\`
2. Configure Serilog (rolling log, 7-day retention)
3. Build DI `ServiceCollection` → store as `App.Services`
4. Initialize database
5. Initialize notification helper
6. Show MainWindow

**DI registrations**:
| Service | Lifetime | Implementation |
|---------|----------|----------------|
| `IKeyProtector` | Singleton | `DpapiKeyProtector` |
| `ICredentialEncryptionService` | Singleton | `CredentialEncryptionService` |
| `IPasswordAuthService` | Singleton | `PasswordAuthService` |
| `IOAuthService` | Singleton | `OAuthService` |
| `HttpClient` | Singleton | `HttpClient` |
| `IAutoDiscoveryService` | Singleton | `AutoDiscoveryService` |
| `IImapConnectionService` | Singleton | `ImapConnectionService` |
| `ISmtpConnectionService` | Singleton | `SmtpConnectionService` |
| `IImapConnectionPool` | Singleton | `ImapConnectionPool` |
| `ISyncManager` | Singleton | `SyncManager` |
| `IDbContextFactory` | Scoped | `MailAggregatorDbContext` |
| `IEmailSyncService` | Scoped | `EmailSyncService` |
| `IEmailSendService` | Scoped | `EmailSendService` |
| `IAccountService` | Scoped | `AccountService` |
| `MainViewModel` | Transient | `MainViewModel` |
| `MainWindow` | Transient | `MainWindow` |

**Shutdown** (`OnExit`): Stop SyncManager → Release pool → Flush Serilog

---

## Styles — `Resources/Styles.xaml`

- **4 converters**: BoolToFontWeight, BoolToVisibility, NullToVisibility, FileSize
- **9 color brushes**: PrimaryBrush (#0078D4), SidebarBrush, SeparatorBrush, UnreadBrush, ReadBrush, SelectedItemBrush, ErrorBrush, etc.
- **Button styles**: PrimaryButton (blue), ToolbarButton (transparent)
- **Control styles**: FolderTreeItem, EmailListItem, StatusBarText

---

## MainWindow — `MainWindow.xaml` + `MainWindow.xaml.cs`

**3-pane layout**:
```
┌──────────────────────────────────────────────────┐
│  Toolbar: Unified Inbox | New | Reply | Forward  │
│  | Delete | Sync | Account Settings             │
├──────────────────────────────────────────────────┤
│  StatusBar: StatusText + sync progress           │
├───────────────┬──────────────────────────────────┤
│  Folder Tree  │  Email List (≤200, date desc)    │
│               ├──────────────────────────────────┤
│  • Account A  │  Email Preview (WebView2 HTML)   │
│    └ Inbox(3) │                                  │
│    └ Sent     │                                  │
└───────────────┴──────────────────────────────────┘
```

**WebView2**: Render HTML email body (fallback: `<pre>` for plaintext)
- Security: disable scripts, disable context menu, block external navigation, block external resources

**Code-behind**:
- `MainWindow_Loaded`: init WebView2 + load accounts (parallel)
- `FolderTreeView_SelectedItemChanged` → `SelectFolderCommand`
- `ViewModel_PropertyChanged` → watch `SelectedEmail` → update preview
- `UpdateEmailPreview`: safe WebView2 HTML/text rendering

---

## MainViewModel — `ViewModels/MainViewModel.cs`

**Key properties**:
- `FolderTree`: `ObservableCollection<AccountFolderNode>` — account/folder hierarchy
- `Emails`: `ObservableCollection<EmailMessage>` — current folder (≤200)
- `SelectedEmail` / `SelectedFolder` / `StatusText` / `IsSyncing` / `Accounts`

**Key commands**:
| Command | Action |
|---------|--------|
| `LoadAccountsCommand` | Load all → sync folders → build tree → start background sync. Per-account folder sync is wrapped in try/catch so one account's IMAP failure doesn't block others |
| `SelectFolderCommand` | Incremental sync → load ≤200 emails (no body) |
| `ShowUnifiedInboxCommand` | Sync all Inboxes → merge display |
| `MarkAsReadCommand` | Mark as read |
| `DeleteMessageCommand` | Move to Trash |
| `ComposeNewCommand` | Open compose window |
| `Reply/ReplyAll/ForwardCommand` | Open compose for reply/forward |
| `OpenAccountSettingsCommand` | Open account management |

**Email selection flow**: `SelectedEmail` change → `LoadFullMessageAndMarkReadAsync()`:
1. Query body from DB (if cached) → 2. If not cached, or if cached HTML contains unresolved `cid:` references (legacy data from before inline image resolution) → `FetchMessageBodyAsync()` from IMAP → 3. Fill BodyHtml/BodyText + Attachments → 4. Auto mark read

**New email event**: `OnNewEmailsReceived()` → UI thread → Toast notification → insert at list top

**Nested class**: `AccountFolderNode : ObservableObject` — for TreeView binding

---

## AddAccountWindow — `Views/AddAccountWindow.xaml` + `ViewModels/AddAccountViewModel.cs`

**5-step wizard**:
| Step | Content |
|------|---------|
| 0 | Enter email address |
| 1 | Auto-discovery in progress |
| 2 | Choose auth (OAuth 2.0 / Password), show discovered server config |
| 3 | Manual server config (IMAP/SMTP + SOCKS5 proxy) |
| 4 | Complete |

**OAuth flow** (`RunOAuthFlowAsync`):
1. `FindProviderByHost()` → 2. `PrepareAuthorization()` → 3. Open browser → 4. `WaitForAuthorizationCodeAsync()` → 5. `ExchangeCodeForTokenAsync()` → 6. Store encrypted tokens

---

## ComposeWindow — `Views/ComposeWindow.xaml` + `ViewModels/ComposeViewModel.cs`

- Non-modal, 4 modes: `New` / `Reply` / `ReplyAll` / `Forward`
- Sender dropdown (multi-account), attachment management
- `SendCommand` → call `IEmailSendService` based on mode
- `PrepareReply` → fill To/Cc, quote original

---

## AccountListWindow — `Views/AccountListWindow.xaml` + `ViewModels/AccountListViewModel.cs`

- Modal dialog, account list (email, host, auth type, enabled status)
- Operations: add, edit, delete (with confirmation)

---

## Converters — `Converters/`

| Converter | Purpose |
|-----------|---------|
| `BoolToFontWeightConverter` | `!IsRead` → `Bold` |
| `BoolToVisibilityConverter` | Bool → `Visible/Collapsed`, supports `Invert` |
| `NullToVisibilityConverter` | Non-null → `Visible`, supports `Invert` |
| `FileSizeConverter` | Bytes → "1.5 MB" / "256 KB" |

---

## NotificationHelper — `ViewModels/NotificationHelper.cs`

- Static class, Windows Toast notifications
- `Initialize()` — create NotifyIcon (system tray)
- `ShowNewMailNotification(accountEmail, messageCount)` — 5s bubble
- `Dispose()` — release resources
