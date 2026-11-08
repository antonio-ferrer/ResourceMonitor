using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Markup;
using Microsoft.Web.WebView2.Core;
using ResourceMonitor.Configuration;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Gui.Notifications;
using ResourceMonitor.Instancing;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Storage;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui;

public partial class App : Application
{
    private NotifyIcon? _notifyIcon;

    public MonitoringService MonitoringService { get; } = new();

    public LiveMonitorService LiveMonitor { get; } = new();

    public MonitorSettings Settings { get; set; } = new();

    public string DataDirectory { get; } = AppSettingsStore.GetDataDirectory();

    // Raiz de composição: montados em OnStartup, repassados pra MainWindow -> ViewModels.
    public ITraceLogger TraceLogger { get; private set; } = new NullTraceLogger();

    public AlertEventQueries AlertEventQueries { get; private set; } = new(new NullTraceLogger());

    public TemplateQueries TemplateQueries { get; private set; } = new(new NullTraceLogger());

    public ITrayNotifier TrayNotifier { get; private set; } = null!;

    public bool IsExiting { get; private set; }

    private SingleInstanceGuard? _singleInstanceGuard;
    private GuiActivationSignal? _activationSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ErrorLog.Initialize(Path.Combine(DataDirectory, "logs"));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write("App", "Erro fatal não tratado, o app vai fechar.", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("App", "Exceção numa tarefa em segundo plano.", args.Exception);
            args.SetObserved();
        };

        _singleInstanceGuard = new SingleInstanceGuard();
        if (!_singleInstanceGuard.IsPrimaryInstance)
        {
            var startedMinimized = e.Args.Contains("--minimized");
            using var activation = new GuiActivationSignal();
            activation.RequestActivation();
            _singleInstanceGuard.Dispose(); // libera antes do MessageBox (bloqueia) para não segurar o mutex à toa
            _singleInstanceGuard = null;

            if (!startedMinimized)
            {
                System.Windows.MessageBox.Show(
                    "ResourceMonitor já está em execução (bandeja ou console).",
                    "ResourceMonitor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        _activationSignal = new GuiActivationSignal();
        Task.Run(() => _activationSignal.RunListenerLoop(() => Dispatcher.Invoke(ShowMainWindow)));

        TraceLogger = TraceLoggerFactory.Create(e.Args, Path.Combine(DataDirectory, "logs"));
        AlertEventQueries = new AlertEventQueries(TraceLogger);
        TemplateQueries = new TemplateQueries(TraceLogger);

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

        Task.Run(DeleteLegacyWebViewFolder);

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

        TrayNotifier = new TrayNotifier(_notifyIcon);
    }

    private bool _isShowingErrorDialog;

    // Erro não tratado na thread de UI: registra, avisa e mantém o app aberto — fechar aqui
    // derrubaria também o monitoramento que roda na bandeja. Um aviso por vez, pra um erro
    // repetido não empilhar janelas.
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("App", "Erro não tratado na interface.", e.Exception);
        e.Handled = true;

        if (_isShowingErrorDialog)
        {
            return;
        }

        _isShowingErrorDialog = true;
        try
        {
            System.Windows.MessageBox.Show(
                $"Ocorreu um erro inesperado: {e.Exception.Message}\n\nO ResourceMonitor continua rodando. Detalhes em:\n{ErrorLog.FilePath}",
                "ResourceMonitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _isShowingErrorDialog = false;
        }
    }

    private Task<CoreWebView2Environment>? _webViewEnvironment;

    // Todos os WebView2 do app compartilham um ambiente com a pasta de dados em
    // %LOCALAPPDATA%\ResourceMonitor\WebView2. Sem isso o WebView2 cria "<exe>.WebView2" ao
    // lado do executável — suja a pasta do app e falha numa pasta sem permissão de escrita
    // (ex: Program Files). Criado na primeira vez que alguém pede (thread de UI).
    public Task<CoreWebView2Environment> GetWebViewEnvironmentAsync() =>
        _webViewEnvironment ??= CoreWebView2Environment.CreateAsync(
            userDataFolder: Path.Combine(DataDirectory, "WebView2"));

    // Pasta de dados do WebView2 que versões até a 0.4.0 criavam ao lado do exe (ver
    // GetWebViewEnvironmentAsync). É só cache do próprio app; se não der pra apagar, fica lá.
    private static void DeleteLegacyWebViewFolder()
    {
        try
        {
            var legacyFolder = Path.Combine(
                AppContext.BaseDirectory, Path.GetFileName(Environment.ProcessPath ?? "ResourceMonitor.Gui.exe") + ".WebView2");
            if (Directory.Exists(legacyFolder))
            {
                Directory.Delete(legacyFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
        LiveMonitor.Dispose();
        _activationSignal?.Dispose();
        _singleInstanceGuard?.Dispose();
        base.OnExit(e);
    }
}
