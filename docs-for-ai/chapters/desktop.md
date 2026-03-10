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
| `ITwoFactorCodeService` | Singleton | `TwoFactorCodeService` |
| `ISyncManager` | Singleton | `SyncManager` |
| `IDbContextFactory` | Scoped | `MailAggregatorDbContext` |
| `IEmailSyncService` | Scoped | `EmailSyncService` |
| `IEmailSendService` | Scoped | `EmailSendService` |
| `IAccountService` | Scoped | `AccountService` |
| `ITwoFactorAccountService` | Scoped | `TwoFactorAccountService` |
| `MainViewModel` | Transient | `MainViewModel` |
| `MainWindow` | Transient | `MainWindow` |
| `TwoFactorViewModel` | Transient | `TwoFactorViewModel` |
| `AddTwoFactorViewModel` | Transient | `AddTwoFactorViewModel` |

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

**Server config passthrough**: After discovery/manual entry, the ViewModel builds a `ServerConfiguration` from the UI fields and passes it as `manualConfig` to `AddAccountAsync`, skipping redundant auto-discovery on account creation. The `SelectedAuthType` is also passed explicitly so the user's auth choice is preserved (prevents auto-detection from overriding it).

**OAuth availability tracking**: `UpdateOAuthAvailability(imapHost)` checks `FindProviderByHost` and sets `IsOAuthAvailable`, `IsOAuthSelected`, and `SelectedAuthType`. Called from two places: `OnImapHostChanged` (fires when user edits IMAP host on manual config screen) and after auto-discovery completes (explicit re-check in case the discovered host differs from what triggered the property change).

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

## TwoFactorWindow — `Views/TwoFactorWindow.xaml` + `ViewModels/TwoFactorViewModel.cs`

**Non-modal window** opened from MainWindow toolbar ("2FA" button → `OpenTwoFactorCommand`).

**Layout**: `DockPanel` with ListBox (account list) and bottom button bar (Add/Edit/Delete + status text).

**ListBox item template**: Each item shows Issuer, Label, CurrentCode (Consolas 28pt), ProgressBar with remaining seconds. Copy button per item bound to `CopyCodeCommand` via `RelativeSource AncestorType=ListBox`.

**Empty state**: DataTrigger on `Items.Count == 0` replaces ListBox template with placeholder text.

**Code-behind**: `Loaded` → `vm.InitializeAsync()`, `Closed` → `vm.Dispose()`.

**TwoFactorViewModel** (`ObservableObject`, `IDisposable`):
- Constructor: receives `ITwoFactorCodeService` + `ILogger`, creates `DispatcherTimer` (1s interval)
- `InitializeAsync()`: loads accounts + starts timer
- `Dispose()`: stops timer + unsubscribes tick handler

**Key commands**:
| Command | Action |
|---------|--------|
| `LoadAccountsCommand` | Create scope → `ITwoFactorAccountService.GetAllAsync()` → decrypt secrets → build `TwoFactorDisplayItem` collection |
| `CopyCodeCommand` | Copy `SelectedItem.CurrentCode` (stripped spaces) to clipboard |
| `AddAccountCommand` | Open `AddTwoFactorWindow` (modal) → reload on success |
| `EditAccountCommand` | Open `AddTwoFactorWindow` with `LoadForEdit()` → reload on success |
| `DeleteAccountCommand` | Confirmation dialog → `ITwoFactorAccountService.DeleteAsync()` → remove from list |

**Scope-based service resolution**: `ITwoFactorAccountService` is Scoped, so each command creates a DI scope via `App.Services.CreateScope()` to resolve it (same pattern as other scoped services).

---

## AddTwoFactorWindow — `Views/AddTwoFactorWindow.xaml` + `ViewModels/AddTwoFactorViewModel.cs`

**Modal dialog** for adding or editing 2FA accounts. Title bound to `WindowTitle` (dynamic: "Add 2FA Account" / "Edit 2FA Account").

**Two input modes** (add mode only, toggled by RadioButton):
1. **Manual Input**: Issuer, Label, Secret (Base32), Advanced Settings (Algorithm ComboBox, Digits, Period)
2. **URI Import**: Paste `otpauth://` URI → Parse button → auto-fills all fields

**Edit mode**: Shows only Issuer and Label fields (secret is immutable). Activated via `LoadForEdit(TwoFactorAccount)`.

**Code-behind**: `Loaded` → subscribe to `vm.CloseRequested` → set `DialogResult` + close window.

**AddTwoFactorViewModel** (`ObservableObject`):

**Key commands**:
| Command | Action |
|---------|--------|
| `ParseUriCommand` | `ITwoFactorCodeService.ParseOtpAuthUri()` → populate fields |
| `SaveCommand` | Create scope → add (manual or URI) or update → set `DialogResult = true` → `CloseRequested` |

**CloseRequested pattern**: ViewModel exposes `event Action? CloseRequested`. On save success, fires event. Code-behind subscribes and sets `DialogResult` + calls `Close()`. Same pattern used for modal dialog result passing.

**Validation**: Requires Issuer (always), Secret (add mode only). URI mode delegates validation to `AddFromUriAsync`.

---

## TwoFactorDisplayItem — `ViewModels/TwoFactorDisplayItem.cs`

**Display wrapper** around `TwoFactorAccount` for real-time TOTP display.

**Properties** (`ObservableObject`):
- `Account` — underlying `TwoFactorAccount` entity
- `CurrentCode` — formatted TOTP code (e.g. "123 456" for 6-digit, "1234 5678" for 8-digit)
- `RemainingSeconds` — seconds until code expires
- `ProgressPercentage` — `RemainingSeconds / Period * 100` (drives ProgressBar)

**`UpdateCode()`**: Called every 1s by `TwoFactorViewModel`'s DispatcherTimer. Only regenerates code when period boundary is crossed (remaining went up) or on first call, to avoid unnecessary computation.

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
