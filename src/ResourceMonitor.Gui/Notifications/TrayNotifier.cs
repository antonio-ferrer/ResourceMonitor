using System.Windows.Forms;

namespace ResourceMonitor.Gui.Notifications;

public sealed class TrayNotifier : ITrayNotifier
{
    private readonly NotifyIcon _notifyIcon;

    public TrayNotifier(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    public void ShowWarning(string title, string message) =>
        _notifyIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.Warning);
}
