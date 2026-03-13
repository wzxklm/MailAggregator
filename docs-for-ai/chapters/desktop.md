# Desktop UI — WPF Layer

## Project Config

**`MailAggregator.Desktop.csproj`**: `net8.0-windows`, WinExe, UseWPF+UseWindowsForms (NotifyIcon)
- `ApplicationIcon`: `Resources\app.ico` (EXE icon)
- `EmbeddedResource`: `Resources\app.ico` (loaded at runtime for NotifyIcon via `LoadEmbeddedIcon`)
- Deps: CommunityToolkit.Mvvm 8.4.0, WebView2 1.0.3800.47, ModernWpfUI 0.9.6, DI 8.0.1

---

## App Entry — `App.xaml.cs`

**Startup** (`OnStartup`): Create `%AppData%\MailAggregator\` → Serilog (7-day rolling, controlled by `LogLevelSwitch`) → DI → DB init → notifications → MainWindow

**`LogLevelSwitch`**: Static `LoggingLevelSwitch` (default `Information`). Serilog `.MinimumLevel.ControlledBy(LogLevelSwitch)` enables runtime log level changes. Toggled by `MainViewModel.CycleLogLevelCommand`

**DI registrations**:
- **Singleton**: DbContextFactory, Logger, KeyProtector, CredentialEncryption, PasswordAuth, OAuth, HttpClient, AutoDiscovery, ImapConnection, SmtpConnection, ImapConnectionPool, TwoFactorCodeService, SyncManager
- **Scoped**: DbContext, EmailSyncService, EmailSendService, AccountService, TwoFactorAccountService
- **Transient**: MainViewModel, AccountListViewModel, AddAccountViewModel, ComposeViewModel, TwoFactorViewModel, AddTwoFactorViewModel, MainWindow

**Shutdown**: Stop SyncManager → Release pool → Flush Serilog

---

## Styles — `Resources/Styles.xaml`

Built on **ModernWpf** base styles. `App.xaml` merges `ThemeResources` + `XamlControlsResources` before `Styles.xaml`.

**Semantic color resources**: PrimaryBrush, SuccessBackgroundBrush, SuccessTextBrush, ErrorBackgroundBrush, InfoBackgroundBrush, SubtleBrush, CardBrush, CardBorderBrush

**Styles** (BasedOn ModernWpf where applicable):
- `PrimaryButton` / `AccentButton` — BasedOn `AccentButtonStyle`
- `DangerButton` — red background, BasedOn `DefaultButtonStyle`
- `ToolbarButton` — BasedOn `DefaultButtonStyle`
- `EmailListItem` — BasedOn `DefaultListBoxItemStyle`
- `SectionHeader`, `PageTitle` — typography styles
- `StatusBarText`

Removed legacy: `FolderTreeItem`, `CardStyle`

---

## MainWindow

All windows use `ui:WindowHelper.UseModernWindowStyle="True"` for Fluent window chrome.

```
┌──────────────────────────────────────────────────┐
│  Command bar (Border): icon buttons with         │
│  Segoe MDL2 Assets glyphs + text labels          │
│  Unified Inbox | New | Reply | Forward           │
│  | Delete | Sync | Accounts | 2FA               │
├──────────────────────────────────────────────────┤
│  StatusBar: StatusText + sync progress + log level│
├───────────────┬──────────────────────────────────┤
│  Folder Tree  │  Email List (≤200, date desc)    │
│               ├──────────────────────────────────┤
│  • Account A  │  Email Preview (WebView2 HTML)   │
│    └ Inbox(3) │                                  │
└───────────────┴──────────────────────────────────┘
```

Toolbar replaced from `ToolBarTray` to custom `Border` command bar with `Segoe MDL2 Assets` icon font.

**WebView2**: HTML body (fallback `<pre>` plaintext). Hardened (see `auth.md`)

**Minimize-to-tray**: `OnStateChanged` hides window on minimize. `OnClosing` cancels close and hides (unless `NotificationHelper.IsExitRequested` is set by tray Exit).

**Code-behind**: `Loaded` → init WebView2 + load accounts. `FolderTreeView_SelectedItemChanged` → `SelectFolderCommand`. `SelectedEmail` change → `UpdateEmailPreview`

---

## MainViewModel

**Properties**: `FolderTree` (ObservableCollection<AccountFolderNode>), `Emails` (≤200), `SelectedEmail`, `SelectedFolder`, `StatusText`, `IsSyncing`, `LogLevel` ("INFO"/"DEBUG"), `Accounts`

**Commands**:
| Command | Action |
|---------|--------|
| `LoadAccountsCommand` | Load all → load folders from DB (no IMAP; folders synced once on first connection by SyncManager) → build tree → start sync → show unified inbox (per-account try/catch) |
| `SelectFolderCommand` | Incremental sync → load ≤200 emails (no body) |
| `ShowUnifiedInboxCommand` | Sync all Inboxes → merge (per-account try/catch: one account failure is logged + skipped, others continue) |
| `MarkAsReadCommand` / `DeleteMessageCommand` | Read / Move to Trash |
| `ComposeNewCommand` / `Reply/ReplyAll/ForwardCommand` | Open compose |
| `CycleLogLevelCommand` | Toggle Serilog between INFO ↔ DEBUG via `App.LogLevelSwitch` |
| `OpenAccountSettingsCommand` | Account management |

**Email selection**: `SelectedEmail` → `LoadFullMessageAndMarkReadAsync()`: DB body (if cached) → if not cached or has unresolved `cid:` → IMAP fetch → fill body + attachments → mark read

**New email event**: `OnNewEmailsReceived()` → Dispatcher → Toast → insert at top → update unread counts

**Folders synced event**: `OnFoldersSynced()` → Dispatcher → looks up `AccountFolderNode` by `AccountId` → `GetFoldersFromDbAsync` → `PopulateFolderChildren` (refreshes folder tree when IMAP syncs folders on first connection)

**`PopulateFolderChildren`** (static helper): Clears and repopulates an `AccountFolderNode.Children` from a folder list (Inbox first, then alphabetical). Used by both `LoadAccountsAsync` and `OnFoldersSynced` to avoid duplication.

**Unread counts**: `UpdateUnreadCountsAsync()` queries `Messages` table (grouped `COUNT WHERE !IsRead`), updates `AccountFolderNode.UnreadCount` with change-detection guard. Called on load and new-email sync. `MarkAsReadAsync` decrements locally. XAML badge uses `NullToVisibility` + `FallbackValue=Collapsed`.

**Nested**: `AccountFolderNode : ObservableObject` (`DisplayName`, `UnreadCount`, `Account`, `Folder`, `IsAccount`, `Children`) for TreeView

---

## AddAccountWindow + AddAccountViewModel

**5-step wizard**: 0: email → 1: discovery (`ui:ProgressRing` + "Skip" button) → 2: auth choice (OAuth/Password) → 3: manual config (`ui:ControlHelper.PlaceholderText` for TextBox placeholders) → 4: complete

**Two wizard paths**:
- **Discovery success**: 0 → 1 → 2 → 4. From step 2, "Server Settings" → 3 → Back → 2
- **Discovery fail/skip**: 0 → 1 → 3 → "Next" → 2 → 4. Step 3 shows "Next" (not "Save") in non-edit mode

**Skip discovery**: Step 1 has "Skip — Configure Manually" button. Cancels the in-flight `DiscoverAsync` via `CancellationTokenSource`, jumps to step 3. `IsDiscoverySucceeded` tracks whether discovery found config — controls success banner visibility on step 2 and `GoBack` navigation logic

**GoBack navigation**: Step 2 Back → 0 (discovered) or 3 (manual). Step 3 Back → 2 (discovered) or 0 (manual). Step 1 is transient (never navigated back to)

**Config passthrough**: Builds `ServerConfiguration` from UI fields → `manualConfig` param (skips re-discovery). `SelectedAuthType` passed explicitly.

**OAuth availability**: `UpdateOAuthAvailability(imapHost)` via `FindProviderByHost`. Called on `OnImapHostChanged` and after discovery.

**OAuth flow**: `FindProviderByHost` → `PrepareAuthorization` → browser → `WaitForAuthorizationCodeAsync` → `ExchangeCodeForTokenAsync` → encrypt tokens

---

## ComposeWindow + ComposeViewModel

Non-modal, 4 modes: New/Reply/ReplyAll/Forward. Sender dropdown, attachments. `SendCommand` → `IEmailSendService`. `PrepareReply` fills To/Cc + quote.

---

## AccountListWindow + AccountListViewModel

Modal. Account list (email, host, auth, IDLE/Polling indicator). Add/edit/delete with confirmation.

**Commands**: `AddAccountCommand`, `EditAccountCommand`, `DeleteAccountCommand`, `ToggleIdleCommand` (toggles `Account.UseIdle` → `UpdateAccountAsync` → restarts sync; reverts in-memory state on failure — no LoadAccountsAsync reload needed since the list binding stays live)

---

## TwoFactorWindow + TwoFactorViewModel

Non-modal, opened from toolbar "2FA" button.

**Layout**: DockPanel with ListBox (Issuer, Label, CurrentCode Consolas 28pt, ProgressBar) + button bar. Copy button per item via `RelativeSource AncestorType=ListBox`, `CommandParameter="{Binding}"`. Empty state via DataTrigger.

**Code-behind**: `Loaded` → `InitializeAsync()`, `Closed` → `Dispose()`.

**TwoFactorViewModel** (`ObservableObject`, `IDisposable`): DispatcherTimer 1s. `InitializeAsync()` loads + starts. `Dispose()` stops timer.

**Commands**:
| Command | Action |
|---------|--------|
| `LoadAccountsCommand` | Scope → `GetAllAsync()` → decrypt → build `TwoFactorDisplayItem` list |
| `CopyCodeCommand` | `TwoFactorDisplayItem?` param or `SelectedItem` → clipboard (stripped spaces) |
| `AddAccountCommand` | `AddTwoFactorWindow` modal → reload |
| `EditAccountCommand` | `AddTwoFactorWindow` + `LoadForEdit()` → reload |
| `DeleteAccountCommand` | Confirm → `DeleteAsync()` → remove |

Each command creates DI scope for scoped `ITwoFactorAccountService`.

---

## AddTwoFactorWindow + AddTwoFactorViewModel

Modal dialog. Title dynamic ("Add/Edit 2FA Account").

**Add mode**: Manual (Issuer, Label, Secret, Algorithm, Digits, Period) or URI import (`otpauth://` → Parse → auto-fill)
**Edit mode**: Issuer + Label only (secret immutable). Via `LoadForEdit(TwoFactorAccount)`.

**Code-behind**: `Loaded` → subscribe `CloseRequested` → `DialogResult` + close.

**Commands**: `ParseUriCommand` (parse URI → fill fields), `SaveCommand` (add/update → `CloseRequested`)

**CloseRequested pattern**: VM fires `event Action? CloseRequested` → code-behind sets `DialogResult` + `Close()`.

**Validation**: Issuer required always, Secret required in add mode. URI delegates to `AddFromUriAsync`.

---

## TwoFactorDisplayItem

Wrapper for real-time TOTP display (`ObservableObject`).

**Properties**: `Account`, `CurrentCode` ("123 456" / "1234 5678"), `RemainingSeconds`, `ProgressPercentage` (remaining/period*100)

**`UpdateCode()`**: Called 1s by timer. Regenerates only on period boundary (remaining went up) or first call.

---

## Converters

`BoolToFontWeightConverter` (!IsRead→Bold), `BoolToVisibilityConverter` (supports Invert), `NullToVisibilityConverter` (supports Invert), `FileSizeConverter` (bytes→"1.5 MB")

---

## NotificationHelper

Static. `Initialize()` → NotifyIcon with context menu (Show / Exit) + double-click restore. Icon loaded via `LoadEmbeddedIcon()` (`Assembly.GetManifestResourceStream("MailAggregator.Desktop.Resources.app.ico")`). `RestoreMainWindow()` shows, restores, and activates MainWindow via Dispatcher. `IsExitRequested` flag signals MainWindow to allow close on tray Exit. `ShowNewMailNotification(email, count)` → 5s bubble (click restores). `Dispose()` cleans up icon + context menu.
