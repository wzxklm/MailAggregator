# MainViewModel — Central VM: folder tree, email list, sync, dialog launchers

## Overview

Orchestrates the main UI: loads accounts and folder trees from DB, manages email list with unified inbox and per-folder views, handles sync events, and launches all secondary windows (Compose, AccountList, TwoFactor). Split into two partial class files for readability. Implements `IDisposable` to unsubscribe from `SyncManager` events and cancel in-flight operations.

## File Structure

| File | Content |
|------|---------|
| `MainViewModel.cs` | Fields, constructor, `Dispose`, `InitializeAsync`, `LoadAccountsAsync`, compose/reply/forward commands, settings/log/2FA commands, helper methods |
| `MainViewModel.EmailList.cs` | `SelectFolderAsync`, `ShowUnifiedInboxAsync`, `FilterByAccountAsync`, `LoadEmailsForCurrentViewAsync`, `MarkAsReadAsync`, `DeleteMessageAsync`, `OnSelectedEmailChanged`, `LoadFullMessageAndMarkReadAsync`, event handlers (`OnNewEmailsReceived`, `OnFoldersSynced`, `InsertNewEmailsAsync`) |
| `AccountFolderNode.cs` | Separate class: folder tree node with `DisplayName`, `UnreadCount`, `Account`, `Folder`, `IsAccount`, `Children` |

## Key Behaviors

- **Unified inbox**: Default view aggregates all Inbox folders across accounts. Syncs each account's inbox concurrently, with per-account error isolation
- **Folder selection**: Cancels previous folder load via `CancellationTokenSource` swap, runs incremental sync, then loads messages from DB
- **Lazy body loading**: Email list projection excludes `BodyHtml`/`BodyText`; full body fetched on selection via `LoadFullMessageAndMarkReadAsync`
- **CID re-fetch**: Detects cached HTML with unresolved `cid:` references and re-fetches from IMAP to resolve inline images
- **New email events**: `SyncManager.NewEmailsReceived` handler inserts new messages at top of list via `Dispatcher.Invoke`, shows toast notification
- **Folder refresh events**: `SyncManager.FoldersSynced` handler repopulates folder tree children for the affected account
- **Unread counts**: Queries DB for unread counts per folder, displayed on `AccountFolderNode.UnreadCount`
- **Account reload cancellation**: Opening account settings cancels any in-flight `LoadAccountsAsync` to abort stale IMAP connections (e.g. using old proxy settings)
- **Log level toggle**: `CycleLogLevel` switches Serilog between INFO and DEBUG at runtime via `LoggingLevelSwitch`
- **Message limit**: Email list capped at 200 messages per view
- **Service separation**: Uses `IEmailSyncService` for folder/message sync operations and `IEmailOperationService` for single-message operations (mark read, delete, fetch body)

## Interface

`MainViewModel` (no interface — resolved directly from DI)

Key commands: `LoadAccountsCommand`, `SelectFolderCommand`, `ShowUnifiedInboxCommand`, `FilterByAccountCommand`, `MarkAsReadCommand`, `DeleteMessageCommand`, `ComposeNewCommand`, `ReplyCommand`, `ReplyAllCommand`, `ForwardCommand`, `OpenTwoFactorCommand`, `OpenAccountSettingsCommand`, `CycleLogLevelCommand`

Key properties: `FolderTree`, `Emails`, `SelectedEmail`, `SelectedFolder`, `SelectedFilterAccount`, `Accounts`, `StatusText`, `IsSyncing`, `LogLevel`

## Internal Details

**Concurrency**: Two `CancellationTokenSource` fields:
- `_folderSwitchCts` — cancelled on each folder selection to abort previous folder's sync
- `_loadAccountsCts` — cancelled on each `LoadAccountsAsync` call and when opening account settings

**Dialog launch pattern**: Resolves VM from `App.Services`, creates Window with `DataContext = vm`, calls `Show()` (non-modal) or `ShowDialog()` (modal for AccountList).

## MainWindow (code-behind)

**WebView2 email preview**:
- Scripts disabled, context menus disabled, status bar disabled
- External navigation intercepted and opened in default browser
- External images blocked by default (anti-tracking); `WebResourceRequested` handler returns 403 for `http://`/`https://` image requests unless user clicks "Load Images"
- `RemoteImagesBar` shown when HTML contains external image `src` attributes
- Falls back to `<pre>` wrapped plain text if no HTML body

**Minimize-to-tray**: `OnStateChanged` hides window on minimize; `OnClosing` cancels close and hides unless `NotificationHelper.IsExitRequested` is true.

**Initialization**: `Loaded` event runs WebView2 init and `MainViewModel.InitializeAsync()` concurrently via `Task.WhenAll`.

## App.xaml.cs (entry point)

**Startup sequence**:
1. Creates `%AppData%/MailAggregator` directory
2. Configures Serilog (daily rolling file, 7-day retention)
3. Builds DI container via `ConfigureServices`
4. Runs `DatabaseInitializer.InitializeAsync` (EF migrations)
5. Calls `NotificationHelper.Initialize()` (system tray icon)
6. Resolves and shows `MainWindow`

**DI registrations**:
- DB: `MailAggregatorDbContext` (scoped) + `IDbContextFactory` (singleton)
- Auth: `DpapiKeyProtector`, `CredentialEncryptionService`, `PasswordAuthService`, `OAuthService`
- Connection: `ImapConnectionService`, `ImapConnectionPool`, `SmtpConnectionService`
- Mail: `EmailSyncService` (scoped), `EmailOperationService` (scoped), `EmailSendService` (scoped)
- Account: `AccountService` (scoped)
- 2FA: `TwoFactorCodeService` (singleton), `TwoFactorAccountService` (scoped)
- Sync: `SyncManager` (singleton)
- ViewModels: all Transient
- Windows: `MainWindow` (Transient)

**Shutdown**: Stops all sync, disposes connection pool, disposes DI container, disposes tray icon, flushes Serilog.

## Dependencies

- Uses: `IAccountService`, `IEmailSyncService`, `IEmailOperationService`, `ISyncManager`, `ILogger`, `MailAggregatorDbContext`, `NotificationHelper`
- Used by: `MainWindow` (DataContext)
