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

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Iniciar monitoramento", null, (_, _) => MonitoringService.Start(Settings, DataDirectory));
        menu.Items.Add("Parar monitoramento", null, async (_, _) => await MonitoringService.StopAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitApplication());

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
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
