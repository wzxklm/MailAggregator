# Account ViewModels — Account list management and add/edit wizard

## Overview

Two ViewModels handle account CRUD: `AccountListViewModel` displays accounts with actions (add/edit/delete/toggle IDLE), and `AddAccountViewModel` implements a multi-step wizard for creating or editing mail accounts with auto-discovery and OAuth support.

## AccountListViewModel

### Overview
Modal dialog VM for managing the account list. Opened from `MainViewModel.OpenAccountSettings()` via `ShowDialog()` -- main window reloads accounts on close.

### Key Behaviors
- **Load accounts**: Fetches all accounts via `IAccountService.GetAllAccountsAsync()`
- **Add account**: Opens `AddAccountWindow` as modal dialog; reloads list if `DialogResult == true`
- **Edit account**: Opens `AddAccountWindow` pre-populated via `vm.LoadForEdit(account)`; skips to server config step
- **Toggle IDLE**: Flips `UseIdle` flag and persists via `UpdateAccountAsync`; reverts in-memory state on failure
- **Delete account**: Confirmation dialog, then `DeleteAccountAsync` removes account and all cached data

### Interface
`AccountListViewModel` (no interface)

Commands: `LoadAccountsCommand`, `AddAccountCommand`, `EditAccountCommand`, `ToggleIdleCommand`, `DeleteAccountCommand`

Properties: `Accounts`, `SelectedAccount`, `StatusText`

### Dependencies
- Uses: `IAccountService`, `ILogger`
- Used by: `MainViewModel` (opens dialog)

---

## AddAccountViewModel

### Overview
Multi-step wizard for adding or editing a mail account. Steps: 0=Email input, 1=Discovery, 2=Auth type, 3=Server config, 4=Complete. Edit mode skips directly to step 3.

### Key Behaviors
- **Auto-discovery**: Calls `IAutoDiscoveryService.DiscoverAsync()` to find IMAP/SMTP settings; cancellable via `CancellationTokenSource`. On failure, falls back to manual config (step 3)
- **OAuth availability**: Checked automatically when `ImapHost` changes via `OnImapHostChanged` partial method. Uses `IOAuthService.FindProviderByHost()`. When available, OAuth is pre-selected
- **OAuth flow**: 4-step browser-based flow: prepare auth URL with PKCE, open browser, wait for callback on localhost listener (3-min timeout), exchange code for tokens
- **Password auth**: Direct password entry; `PasswordBox` bound in code-behind (WPF security restriction prevents XAML binding)
- **Edit mode**: `LoadForEdit(account)` sets `IsEditMode=true`, populates fields, jumps to step 3. Save updates existing account without re-running discovery
- **Proxy support**: Optional SOCKS proxy (host + port) applied to account on save
- **Navigation**: `GoBack`, `GoToAuth`, `GoToServerConfig`, `SkipDiscovery` commands with step-aware logic

### Interface
`AddAccountViewModel` (no interface)

Commands: `DiscoverCommand`, `SkipDiscoveryCommand`, `GoToServerConfigCommand`, `GoToAuthCommand`, `GoBackCommand`, `SaveCommand`

Properties: `CurrentStep`, `EmailAddress`, `ImapHost`, `ImapPort`, `ImapEncryption`, `SmtpHost`, `SmtpPort`, `SmtpEncryption`, `SelectedAuthType`, `Password`, `IsOAuthAvailable`, `IsOAuthSelected`, `IsPasswordSelected`, `ProxyHost`, `ProxyPort`, `IsEditMode`, `WindowTitle`, `ErrorMessage`, `IsBusy`

Events: `CloseRequested` (fired on successful save)

### Internal Details
Step navigation logic in `GoBack`:
- From Auth (2): goes to Email (0) if discovery succeeded, else Server Config (3)
- From Server Config (3): goes to Auth (2) if discovery succeeded, else Email (0)

### Dependencies
- Uses: `IAccountService`, `IAutoDiscoveryService`, `IOAuthService`, `ILogger`
- Used by: `AccountListViewModel` (opens dialog)

---

## Views

### AccountListWindow
Code-behind calls `vm.InitializeAsync()` on `Loaded`. Displayed as modal dialog.

### AddAccountWindow
Code-behind wires `vm.CloseRequested` to set `DialogResult` and close. Binds `PasswordBox.PasswordChanged` to `vm.Password` in code-behind (WPF `PasswordBox` cannot be XAML-bound).
