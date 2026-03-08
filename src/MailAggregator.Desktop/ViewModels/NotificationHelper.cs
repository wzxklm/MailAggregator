using System.Windows;
using System.Windows.Interop;

namespace MailAggregator.Desktop.ViewModels;

/// <summary>
/// Helper for showing Windows toast notifications for new email arrivals.
/// Uses WPF's built-in notification icon capability.
/// </summary>
public static class NotificationHelper
{
    private static System.Windows.Forms.NotifyIcon? _notifyIcon;

    public static void Initialize()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Visible = true,
            Text = "MailAggregator"
        };

        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        };
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
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}
