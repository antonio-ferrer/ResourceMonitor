using System.Drawing;
using System.Windows.Forms;
using System.Windows.Threading;
using ResourceMonitor.Gui.Notifications;

namespace ResourceMonitor.Gui.Services;

// Ícone da bandeja: menu com o estado atual, Abrir / Iniciar / Parar / Sair, e os balões de
// aviso (Notifier). Criado na thread de UI.
public sealed class TrayIconController : IDisposable
{
    private readonly MonitoringController _monitoring;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripItem _statusItem;
    private readonly ToolStripItem _startItem;
    private readonly ToolStripItem _stopItem;
    private bool _disposed;

    public TrayIconController(MonitoringController monitoring, Action showMainWindow, Action exitApplication)
    {
        _monitoring = monitoring;

        var menu = new ContextMenuStrip();
        // Linha só informativa (desabilitada), atualizada a cada abertura do menu.
        _statusItem = menu.Items.Add(string.Empty);
        _statusItem.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Abrir", null, (_, _) => showMainWindow());
        _startItem = menu.Items.Add("Iniciar monitoramento", null, (_, _) => _monitoring.StartByUser());
        _stopItem = menu.Items.Add("Parar monitoramento", null, async (_, _) => await _monitoring.StopByUserAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exitApplication());
        menu.Opening += (_, _) => UpdateStatus();

        // Assembly.Location aponta pro .dll gerenciado, que não carrega o ícone do ApplicationIcon
        // (isso fica embutido no apphost .exe) — por isso extrai do processo em execução.
        var exePath = Environment.ProcessPath;
        var appIcon = (exePath is not null ? Icon.ExtractAssociatedIcon(exePath) : null) ?? SystemIcons.Application;

        _notifyIcon = new NotifyIcon
        {
            Icon = appIcon,
            Visible = true,
            Text = "ResourceMonitor",
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => showMainWindow();

        Notifier = new TrayNotifier(_notifyIcon);

        // O texto ao passar o mouse no ícone também mostra o estado. RunningStateChanged pode
        // vir de outra thread (loop que parou por falha), daí o InvokeAsync.
        var dispatcher = Dispatcher.CurrentDispatcher;
        _monitoring.Service.RunningStateChanged += (_, _) => dispatcher.InvokeAsync(UpdateStatus);
        UpdateStatus();
    }

    public ITrayNotifier Notifier { get; }

    private void UpdateStatus()
    {
        if (_disposed)
        {
            return;
        }

        var running = _monitoring.IsRunning;
        _statusItem.Text = running ? "● Monitorando" : "○ Parado";
        _startItem.Enabled = !running;
        _stopItem.Enabled = running;
        _notifyIcon.Text = running ? "ResourceMonitor — monitorando" : "ResourceMonitor — parado";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
