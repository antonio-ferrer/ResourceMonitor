using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using ResourceMonitor.Configuration;
using ResourceMonitor.Monitoring;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui;

public partial class App : Application
{
    private NotifyIcon? _notifyIcon;

    public MonitoringService MonitoringService { get; } = new();

    public MonitorSettings Settings { get; set; } = new();

    public string DataDirectory { get; } = AppSettingsStore.GetDataDirectory();

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = AppSettingsStore.Load();

        SetupTrayIcon();

        // Passado pela entrada de registro de "iniciar com o Windows" (ver AutoStartManager) —
        // sobe direto pra bandeja, já monitorando, sem popup de janela.
        var startMinimized = e.Args.Contains("--minimized");
        if (startMinimized)
        {
            MonitoringService.Start(Settings, DataDirectory);
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        if (!startMinimized)
        {
            mainWindow.Show();
        }
    }

    private void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Iniciar monitoramento", null, (_, _) => MonitoringService.Start(Settings, DataDirectory));
        menu.Items.Add("Parar monitoramento", null, async (_, _) => await MonitoringService.StopAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitApplication());

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
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    public void ExitApplication()
    {
        IsExiting = true;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MonitoringService.Dispose();
        base.OnExit(e);
    }
}
