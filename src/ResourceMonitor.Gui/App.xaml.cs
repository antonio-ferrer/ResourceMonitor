using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Markup;
using ResourceMonitor.Configuration;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Storage;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui;

public partial class App : Application
{
    private NotifyIcon? _notifyIcon;

    public MonitoringService MonitoringService { get; } = new();

    public MonitorSettings Settings { get; set; } = new();

    public string DataDirectory { get; } = AppSettingsStore.GetDataDirectory();

    // Raiz de composição: montados em OnStartup, repassados pra MainWindow -> ViewModels.
    public ITraceLogger TraceLogger { get; private set; } = new NullTraceLogger();

    public AlertEventQueries AlertEventQueries { get; private set; } = new(new NullTraceLogger());

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        TraceLogger = TraceLoggerFactory.Create(e.Args, Path.Combine(DataDirectory, "logs"));
        AlertEventQueries = new AlertEventQueries(TraceLogger);

        TraceLogger.Trace("App", $"OnStartup iniciado. Args=[{string.Join(", ", e.Args)}] DataDirectory='{DataDirectory}'");
        TraceLogger.Trace("App", $"Cultura da thread ANTES do override: {CultureInfo.CurrentCulture.Name} (dd/MM/yyyy? {CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern})");

        // App é 100% em português — fixa a cultura em vez de depender da config regional
        // do Windows. WPF por padrão formata StringFormat de binding com Language="en-US"
        // do elemento (não a cultura da thread), daí o override do FrameworkElement.LanguageProperty.
        var culture = new CultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        // DefaultThreadCurrentCulture só vale pra threads NOVAS — a thread de UI já existe
        // nesse ponto (herdou en-GB/o que for do Windows), então precisa setar direto nela também.
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement), new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        TraceLogger.Trace("App", $"Cultura da thread DEPOIS do override: {CultureInfo.CurrentCulture.Name} (dd/MM/yyyy? {CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern}); FrameworkElement.Language default agora = {FrameworkElement.LanguageProperty.GetMetadata(typeof(FrameworkElement)).DefaultValue}");

        Settings = AppSettingsStore.Load();
        TraceLogger.Trace("App", $"Settings carregado. LogDirectory='{Settings.LogDirectory}' databasePath calculado='{Path.Combine(DataDirectory, Settings.LogDirectory, "resourcemonitor.db")}'");

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
