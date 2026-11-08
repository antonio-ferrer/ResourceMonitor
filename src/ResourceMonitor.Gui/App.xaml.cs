using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using ResourceMonitor.Configuration;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Gui.Services;
using ResourceMonitor.Gui.ViewModels;
using ResourceMonitor.Instancing;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Storage;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui;

public partial class App : Application
{
    private readonly MonitoringService _monitoringService = new();
    private readonly LiveMonitorService _liveMonitor = new();
    private readonly string _dataDirectory = AppSettingsStore.GetDataDirectory();

    private SingleInstanceGuard? _singleInstanceGuard;
    private GuiActivationSignal? _activationSignal;
    private TrayIconController? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ErrorLog.Initialize(Path.Combine(_dataDirectory, "logs"));
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

        var traceLogger = TraceLoggerFactory.Create(e.Args, Path.Combine(_dataDirectory, "logs"));

        traceLogger.Trace("App", $"OnStartup iniciado. Args=[{string.Join(", ", e.Args)}] DataDirectory='{_dataDirectory}'");
        traceLogger.Trace("App", $"Cultura da thread ANTES do override: {CultureInfo.CurrentCulture.Name} (dd/MM/yyyy? {CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern})");

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

        traceLogger.Trace("App", $"Cultura da thread DEPOIS do override: {CultureInfo.CurrentCulture.Name} (dd/MM/yyyy? {CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern}); FrameworkElement.Language default agora = {FrameworkElement.LanguageProperty.GetMetadata(typeof(FrameworkElement)).DefaultValue}");

        // Raiz de composição: tudo que a janela e os ViewModels usam é montado aqui e passado
        // pelos construtores — ninguém mais busca dependências em Application.Current.
        var settings = new SettingsSession(_dataDirectory, AppSettingsStore.GetSettingsFilePath());
        traceLogger.Trace("App", $"Settings carregado. LogDirectory='{settings.Current.LogDirectory}' databasePath calculado='{settings.DatabasePath}'");

        var monitoring = new MonitoringController(_monitoringService, settings, MonitoringStateStore.GetFilePath());
        _trayIcon = new TrayIconController(monitoring, ShowMainWindow, ExitApplication);
        var webViewHost = new WebViewHost(Path.Combine(_dataDirectory, "WebView2"));
        var dialogs = new WpfDialogService();
        var alertEventQueries = new AlertEventQueries(traceLogger);
        var templateQueries = new TemplateQueries(traceLogger);

        Task.Run(WebViewHost.DeleteLegacyFolder);

        // Volta como o usuário deixou: monitorando ou parado (ver MonitoringStateStore). Vale
        // também pro boot com o Windows.
        monitoring.StartIfLeftRunning();

        Func<string> getDatabasePath = () => settings.DatabasePath;
        var mainWindow = new MainWindow(
            new HomeViewModel(_monitoringService, () => settings.Current, _liveMonitor),
            new MonitoringViewModel(monitoring, settings, _trayIcon.Notifier, dialogs, new RegistryAutoStart()),
            new TemplatesViewModel(getDatabasePath, templateQueries, dialogs),
            new ChartViewModel(_liveMonitor, getDatabasePath, alertEventQueries),
            new ReportViewModel(getDatabasePath, alertEventQueries),
            _liveMonitor,
            webViewHost,
            traceLogger.IsEnabled);
        MainWindow = mainWindow;

        // Passado pela entrada de registro de "iniciar com o Windows" (ver AutoStartManager) —
        // sobe direto pra bandeja, sem popup de janela.
        if (!e.Args.Contains("--minimized"))
        {
            mainWindow.Show();
        }
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

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void ExitApplication()
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.SaveSettingsIfChanged();
            mainWindow.IsExiting = true;
        }

        _trayIcon?.Dispose();
        Shutdown();
    }

    // Logoff/desligamento do Windows com o app aberto: grava o que estava editado.
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        (MainWindow as MainWindow)?.SaveSettingsIfChanged();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _monitoringService.Dispose();
        _liveMonitor.Dispose();
        _activationSignal?.Dispose();
        _singleInstanceGuard?.Dispose();
        base.OnExit(e);
    }
}
