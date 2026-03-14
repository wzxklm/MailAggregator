# Infrastructure — NotificationHelper, converters, styles

## Overview

Shared UI infrastructure: system tray integration, XAML value converters, and the global style/color resource dictionary. These are cross-cutting concerns used by all views.

## NotificationHelper

### Overview
Static helper managing the Windows system tray icon (`NotifyIcon`), toast notifications, and minimize-to-tray behavior. Initialized once at startup, disposed on exit.

### Key Behaviors
- **System tray icon**: Loads embedded `app.ico` from assembly resources. Context menu: "Show" (restore) and "Exit" (shutdown). Double-click restores main window
- **Toast notifications**: `ShowNewMailNotification(email, count)` shows balloon tip for 5 seconds. Clicking balloon restores main window
- **Exit control**: `IsExitRequested` flag set by tray "Exit" menu item. `MainWindow.OnClosing` checks this flag -- if false, close is cancelled and window is hidden instead (minimize-to-tray)
- **Restore**: `RestoreMainWindow()` calls `Show()`, sets `WindowState.Normal`, and `Activate()` via `Dispatcher.Invoke`

### Interface
Static methods: `Initialize()`, `RestoreMainWindow()`, `ShowNewMailNotification(email, count)`, `Dispose()`

Static property: `IsExitRequested`

### Dependencies
- Uses: `System.Windows.Forms.NotifyIcon`, embedded resource `MailAggregator.Desktop.Resources.app.ico`
- Used by: `App.xaml.cs` (init/dispose), `MainWindow` (close interception), `MainViewModel` (new mail notifications)

---

## Value Converters

All converters are one-way (throw `NotSupportedException` on `ConvertBack`). Registered as static resources in `Styles.xaml`.

| Converter | Key | Input | Output | Notes |
|-----------|-----|-------|--------|-------|
| `BoolToVisibilityConverter` | `BoolToVisibility` | `bool` | `Visibility` | `true`=Visible, `false`=Collapsed. Pass `"Invert"` as parameter for reverse |
| `BoolToFontWeightConverter` | `BoolToFontWeight` | `bool` (IsRead) | `FontWeight` | `true`(read)=Normal, `false`(unread)=Bold |
| `FileSizeConverter` | `FileSizeConverter` | `long` (bytes) | `string` | Human-readable: "1.2 KB", "3.4 MB". Uses B/KB/MB/GB units |
| `NullToVisibilityConverter` | `NullToVisibility` | `object?` | `Visibility` | Non-null=Visible, null=Collapsed. Pass `"Invert"` as parameter for reverse |

### Dependencies
- Uses: WPF `IValueConverter` interface
- Used by: All XAML views (via `StaticResource` keys)

---

## Styles.xaml

Resource dictionary merged into `App.xaml` via `ResourceDictionary.MergedDictionaries` (after ModernWpf theme resources).

### Converter Registrations
All four converters registered as `StaticResource` keys: `BoolToFontWeight`, `BoolToVisibility`, `NullToVisibility`, `FileSizeConverter`.

### Semantic Color Brushes

| Key | Color | Purpose |
|-----|-------|---------|
| `PrimaryBrush` | #0078D4 | Accent color |
| `PrimaryHoverBrush` | #106EBE | Accent hover state |
| `SidebarBrush` | #F5F5F5 | Sidebar background |
| `SeparatorBrush` | #E0E0E0 | Borders and dividers |
| `UnreadBrush` | #1A1A1A | Unread email text |
| `ReadBrush` | #707070 | Read email text, status bar |
| `SelectedItemBrush` | #E5F1FB | Selected item background |
| `ErrorBrush` / `ErrorBackgroundBrush` | #C42B1C / #FFEBEE | Error text and backgrounds |
| `WarningBackgroundBrush` / `WarningBorderBrush` / `WarningTextBrush` | #FFF8E1 / #FFE082 / #5D4037 | Warning states |
| `SuccessBackgroundBrush` / `SuccessTextBrush` | #E8F5E9 / #2E7D32 | Success states |
| `SubtleBrush` | #9E9E9E | De-emphasized text |
| `CardBrush` / `CardBorderBrush` | #FFFFFF / #E8E8E8 | Card surfaces |

### Named Styles

| Key | TargetType | BasedOn | Purpose |
|-----|------------|---------|---------|
| `PrimaryButton` | `Button` | `AccentButtonStyle` | Main action button, padded 16x8 |
| `ToolbarButton` | `Button` | `DefaultButtonStyle` | Toolbar actions, padded 10x6 |
| `DangerButton` | `Button` | `DefaultButtonStyle` | Destructive actions, red foreground |
| `EmailListItem` | `ListBoxItem` | `DefaultListBoxItemStyle` | Email row with bottom separator |
| `StatusBarText` | `TextBlock` | -- | 12px gray status text |
| `SectionHeader` | `TextBlock` | -- | 13px semibold section label |
| `PageTitle` | `TextBlock` | -- | 24px semibold page heading |

### Dependencies
- Uses: ModernWpf theme (`AccentButtonStyle`, `DefaultButtonStyle`, `DefaultListBoxItemStyle`)
- Used by: All XAML views (via `StaticResource` keys)
