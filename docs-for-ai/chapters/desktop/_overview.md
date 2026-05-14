# Desktop UI — WPF presentation layer (MVVM + CommunityToolkit.Mvvm)

## Files

| File | Chapter | Responsibility |
|------|---------|----------------|
| `src/MailAggregator.Desktop/App.xaml.cs` | [main-viewmodel.md](main-viewmodel.md) | DI container setup, DB init, Serilog config, app lifecycle |
| `src/MailAggregator.Desktop/App.xaml` | [infrastructure.md](infrastructure.md) | Merges ModernWpf theme + `Styles.xaml` resource dictionary |
| `src/MailAggregator.Desktop/MainWindow.xaml.cs` | [main-viewmodel.md](main-viewmodel.md) | Main window code-behind: WebView2 email preview, folder tree, minimize-to-tray |
| `src/MailAggregator.Desktop/ViewModels/MainViewModel.cs` | [main-viewmodel.md](main-viewmodel.md) | Central VM core: fields, constructor, account loading, commands, dialog launchers |
| `src/MailAggregator.Desktop/ViewModels/MainViewModel.EmailList.cs` | [main-viewmodel.md](main-viewmodel.md) | Central VM partial: email list, folder selection, unified inbox, event handlers |
| `src/MailAggregator.Desktop/ViewModels/AccountFolderNode.cs` | [main-viewmodel.md](main-viewmodel.md) | Folder tree node model: DisplayName, UnreadCount, Account, Folder, Children |
| `src/MailAggregator.Desktop/ViewModels/AccountListViewModel.cs` | [account-viewmodels.md](account-viewmodels.md) | Account list CRUD, toggle IDLE/polling |
| `src/MailAggregator.Desktop/ViewModels/AddAccountViewModel.cs` | [account-viewmodels.md](account-viewmodels.md) | Multi-step wizard: discovery, auth (password/OAuth), server config |
| `src/MailAggregator.Desktop/ViewModels/ComposeViewModel.cs` | [compose-viewmodel.md](compose-viewmodel.md) | Compose/reply/forward email, attachments, send via SMTP |
| `src/MailAggregator.Desktop/ViewModels/TwoFactorViewModel.cs` | [two-factor-viewmodels.md](two-factor-viewmodels.md) | 2FA code list, 1-second timer refresh, copy-to-clipboard |
| `src/MailAggregator.Desktop/ViewModels/AddTwoFactorViewModel.cs` | [two-factor-viewmodels.md](two-factor-viewmodels.md) | Add/edit 2FA account: manual entry or `otpauth://` URI parse |
| `src/MailAggregator.Desktop/ViewModels/TwoFactorDisplayItem.cs` | [two-factor-viewmodels.md](two-factor-viewmodels.md) | Per-account TOTP display: code generation, countdown, progress |
| `src/MailAggregator.Desktop/ViewModels/NotificationHelper.cs` | [infrastructure.md](infrastructure.md) | System tray icon, toast notifications, minimize-to-tray lifecycle |
| `src/MailAggregator.Desktop/Views/AccountListWindow.xaml.cs` | [account-viewmodels.md](account-viewmodels.md) | Code-behind: calls `InitializeAsync` on load |
| `src/MailAggregator.Desktop/Views/AddAccountWindow.xaml.cs` | [account-viewmodels.md](account-viewmodels.md) | Code-behind: wires `CloseRequested`, binds `PasswordBox` |
| `src/MailAggregator.Desktop/Views/ComposeWindow.xaml.cs` | [compose-viewmodel.md](compose-viewmodel.md) | Code-behind: wires `CloseRequested` |
| `src/MailAggregator.Desktop/Views/TwoFactorWindow.xaml.cs` | [two-factor-viewmodels.md](two-factor-viewmodels.md) | Code-behind: calls `InitializeAsync`, disposes VM on close |
| `src/MailAggregator.Desktop/Views/AddTwoFactorWindow.xaml.cs` | [two-factor-viewmodels.md](two-factor-viewmodels.md) | Code-behind: wires `CloseRequested` + `DialogResult` |
| `src/MailAggregator.Desktop/Converters/BoolToVisibilityConverter.cs` | [infrastructure.md](infrastructure.md) | `bool` to `Visibility`, supports "Invert" parameter |
| `src/MailAggregator.Desktop/Converters/BoolToFontWeightConverter.cs` | [infrastructure.md](infrastructure.md) | `IsRead` bool to Bold/Normal font weight |
| `src/MailAggregator.Desktop/Converters/FileSizeConverter.cs` | [infrastructure.md](infrastructure.md) | Bytes to human-readable size string (B/KB/MB/GB) |
| `src/MailAggregator.Desktop/Converters/NullToVisibilityConverter.cs` | [infrastructure.md](infrastructure.md) | Null to Collapsed, non-null to Visible, supports "Invert" |
| `src/MailAggregator.Desktop/Resources/Styles.xaml` | [infrastructure.md](infrastructure.md) | Semantic color brushes, button/list/text styles, converter registrations |

## Overview

WPF desktop client using CommunityToolkit.Mvvm (source-generated `[ObservableProperty]`, `[RelayCommand]`). DI via `Microsoft.Extensions.DependencyInjection` configured in `App.xaml.cs`. All ViewModels are registered as Transient. Dialog windows use VM-driven `CloseRequested` event pattern. `MainWindow` hosts a WebView2 email preview with external image blocking. System tray integration via `NotificationHelper` enables minimize-to-tray and new-mail toast notifications.
