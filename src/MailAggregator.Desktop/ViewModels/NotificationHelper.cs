using System.Reflection;
using System.Windows;

namespace MailAggregator.Desktop.ViewModels;

/// <summary>
/// Helper for showing Windows toast notifications and system tray icon.
/// Supports minimize-to-tray and restore from tray.
/// </summary>
public static class NotificationHelper
{
    private static System.Windows.Forms.NotifyIcon? _notifyIcon;

    public static void Initialize()
    {
        var icon = LoadEmbeddedIcon();

        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        contextMenu.Items.Add("Show", null, (_, _) => RestoreMainWindow());
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) =>
        {
            // Set flag so MainWindow.OnClosing won't intercept
            IsExitRequested = true;
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                System.Windows.Application.Current.Shutdown());
        });

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "MailAggregator",
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.DoubleClick += (_, _) => RestoreMainWindow();

        _notifyIcon.BalloonTipClicked += (_, _) => RestoreMainWindow();
    }

    /// <summary>
    /// When true, the application is shutting down via tray Exit — MainWindow should not intercept close.
    /// </summary>
    public static bool IsExitRequested { get; set; }

    public static void RestoreMainWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var mainWindow = System.Windows.Application.Current.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        });
    }

    public static void ShowNewMailNotification(string accountEmail, int messageCount)
    {
        _notifyIcon?.ShowBalloonTip(
            5000,
            "New Mail",
            $"{messageCount} new message(s) in {accountEmail}",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    public static void Dispose()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }

    private static System.Drawing.Icon? LoadEmbeddedIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("MailAggregator.Desktop.Resources.app.ico");
        return stream != null ? new System.Drawing.Icon(stream) : null;
    }
}
