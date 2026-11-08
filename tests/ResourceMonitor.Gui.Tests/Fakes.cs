using ResourceMonitor.Gui.Notifications;
using ResourceMonitor.Gui.Services;

namespace ResourceMonitor.Gui.Tests;

// Responde no lugar do usuário e anota o que foi perguntado.
internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; }

    public string? SaveFileAnswer { get; set; }

    public List<string> Confirmations { get; } = new();

    public List<string> Warnings { get; } = new();

    public bool Confirm(string title, string message)
    {
        Confirmations.Add(message);
        return ConfirmAnswer;
    }

    public void ShowWarning(string title, string message) => Warnings.Add(message);

    public string? PickSaveFile(string filter, string defaultFileName) => SaveFileAnswer;
}

internal sealed class FakeAutoStart : IAutoStart
{
    private bool _enabled;

    public FakeAutoStart(bool enabled) => _enabled = enabled;

    public List<bool> SetCalls { get; } = new();

    public bool IsEnabled() => _enabled;

    public void SetEnabled(bool enabled)
    {
        SetCalls.Add(enabled);
        _enabled = enabled;
    }
}

internal sealed class FakeTrayNotifier : ITrayNotifier
{
    public void ShowWarning(string title, string message)
    {
    }
}
