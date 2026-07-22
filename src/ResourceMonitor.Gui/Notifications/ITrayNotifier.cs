namespace ResourceMonitor.Gui.Notifications;

public interface ITrayNotifier
{
    void ShowWarning(string title, string message);
}
