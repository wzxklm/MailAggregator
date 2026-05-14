# Two-Factor ViewModels — TOTP authenticator UI

## Overview

Three classes implement the built-in 2FA authenticator: `TwoFactorViewModel` manages the code list with real-time countdown, `TwoFactorDisplayItem` wraps each account with live code generation, and `AddTwoFactorViewModel` handles add/edit with manual entry or `otpauth://` URI parsing.

## TwoFactorViewModel

### Overview
Main 2FA window VM. Displays all TOTP accounts with live-updating codes. Uses a 1-second `DispatcherTimer` to refresh codes and countdown progress. Implements `IDisposable` to stop timer.

### Key Behaviors
- **Timer-driven refresh**: `DispatcherTimer` (1-second interval) calls `UpdateCode()` on every `TwoFactorDisplayItem` each tick
- **Load accounts**: Resolves scoped `ITwoFactorAccountService`, fetches all accounts, decrypts secrets, creates `TwoFactorDisplayItem` instances
- **Copy code**: Strips space formatting from code, copies to clipboard via `System.Windows.Clipboard.SetText`
- **Add account**: Opens `AddTwoFactorWindow` as modal dialog; reloads list on `DialogResult == true`
- **Edit account**: Opens `AddTwoFactorWindow` with `vm.LoadForEdit(account)`; reloads on close
- **Delete account**: Confirmation dialog, then removes via `ITwoFactorAccountService.DeleteAsync`

### Interface
`TwoFactorViewModel` (no interface)

Commands: `LoadAccountsCommand`, `CopyCodeCommand`, `AddAccountCommand`, `EditAccountCommand`, `DeleteAccountCommand`

Properties: `Items`, `SelectedItem`, `StatusText`

### Dependencies
- Uses: `ITwoFactorCodeService`, `ITwoFactorAccountService` (scoped), `ILogger`
- Used by: `MainViewModel` (opens dialog via `OpenTwoFactor` command)

---

## TwoFactorDisplayItem

### Overview
Observable wrapper around a `TwoFactorAccount` that generates and formats TOTP codes. Created by `TwoFactorViewModel` with the decrypted secret.

### Key Behaviors
- **Code regeneration**: `UpdateCode()` called every second by parent timer. Only regenerates code when period boundary is crossed (remaining seconds went up) or on first call -- avoids unnecessary string allocation
- **Code formatting**: 6-digit codes formatted as "123 456", 8-digit as "1234 5678"
- **Progress tracking**: `ProgressPercentage` = remaining / period * 100, bound to UI progress bar

### Interface
Properties: `Account` (readonly `TwoFactorAccount`), `CurrentCode`, `RemainingSeconds`, `ProgressPercentage`

Method: `UpdateCode()` — called by timer

### Dependencies
- Uses: `ITwoFactorCodeService` (generates codes, calculates remaining seconds)
- Used by: `TwoFactorViewModel` (items collection)

---

## AddTwoFactorViewModel

### Overview
Add/edit dialog VM for 2FA accounts. Supports two input modes: manual entry (issuer + label + secret + algorithm + digits + period) and `otpauth://` URI paste-and-parse.

### Key Behaviors
- **URI mode**: `ParseUri` command calls `ITwoFactorCodeService.ParseOtpAuthUri()` to extract all fields from a standard `otpauth://totp/` URI
- **Manual mode**: Direct field entry with validation (issuer and secret required)
- **Edit mode**: `LoadForEdit(account)` sets `IsEditMode=true`, populates issuer and label. Edit only allows changing issuer/label (secret is immutable after creation)
- **Save**: Creates via `ITwoFactorAccountService.AddFromUriAsync` (URI mode) or `AddAsync` (manual), updates via `UpdateAsync` (edit mode). Fires `CloseRequested` on success

### Interface
`AddTwoFactorViewModel` (no interface)

Commands: `ParseUriCommand`, `SaveCommand`

Properties: `Issuer`, `Label`, `Secret`, `UriText`, `IsUriMode`, `SelectedAlgorithm`, `Digits`, `Period`, `StatusText`, `IsEditMode`, `WindowTitle`

Events: `CloseRequested` (fired on successful save)

### Dependencies
- Uses: `ITwoFactorCodeService`, `ITwoFactorAccountService` (scoped), `ILogger`
- Used by: `TwoFactorViewModel` (opens dialog)

---

## Views

### TwoFactorWindow
Code-behind calls `vm.InitializeAsync()` on `Loaded` and `vm.Dispose()` on `Closed` (stops timer). Non-modal window.

### AddTwoFactorWindow
Code-behind wires `vm.CloseRequested` to set `DialogResult` and close. Modal dialog.
