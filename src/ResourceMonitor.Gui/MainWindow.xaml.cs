using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using ResourceMonitor.Gui.ViewModels;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui;

public partial class MainWindow : Window
{
    private readonly HomeViewModel _homeViewModel;
    private readonly MonitoringViewModel _monitoringViewModel;
    private readonly DataViewModel _dataViewModel;
    private readonly ChartViewModel _chartViewModel;
    private readonly ReportViewModel _reportViewModel;
    private readonly OffendersViewModel _offendersViewModel;

    private bool _homeWebViewReady;
    private string? _pendingHomeJson;

    private bool _liveWebViewReady;
    private string? _pendingLiveJson;

    private bool _trendWebViewReady;
    private string? _pendingTrendJson;

    private bool _reportWebViewReady;
    private string? _pendingReportJson;

    private bool _eventsWebViewReady;
    private string? _pendingEventsJson;

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;

        if (app.TraceLogger.IsEnabled)
        {
            Title += " - modo depuração";
        }

        _homeViewModel = new HomeViewModel(app.MonitoringService, app.Settings, app.LiveMonitor);
        _monitoringViewModel = new MonitoringViewModel(app.MonitoringService, app.Settings, app.DataDirectory, app.TrayNotifier);
        _dataViewModel = new DataViewModel(app.MonitoringService, GetDatabasePath, app.AlertEventQueries, app.TraceLogger);
        _chartViewModel = new ChartViewModel(app.LiveMonitor, GetDatabasePath, app.AlertEventQueries);
        _reportViewModel = new ReportViewModel(GetDatabasePath, app.AlertEventQueries);
        _offendersViewModel = new OffendersViewModel(GetDatabasePath, app.AlertEventQueries);

        HomeTabRoot.DataContext = _homeViewModel;
        MonitoringTabRoot.DataContext = _monitoringViewModel;
        DataTabRoot.DataContext = _dataViewModel;
        ChartLiveTabRoot.DataContext = _chartViewModel;
        ChartTrendTabRoot.DataContext = _chartViewModel;
        ChartEventosTabRoot.DataContext = _chartViewModel;
        ReportTabRoot.DataContext = _reportViewModel;
        OffendersTabRoot.DataContext = _offendersViewModel;

        _homeViewModel.LiveSamplesReady += OnHomeLiveSamplesReady;
        _dataViewModel.ViewChartRequested += OnViewChartRequested;
        _chartViewModel.LiveSamplesReady += OnLiveSamplesReady;
        _chartViewModel.DailyTrendReady += OnDailyTrendReady;
        _chartViewModel.TrendWithEventsReady += OnTrendWithEventsReady;
        _reportViewModel.ReportReady += OnReportReady;

        // Só depois de assinar DailyTrendReady acima — chamado dentro do construtor do
        // ChartViewModel dispararia o evento antes de existir alguém escutando.
        _chartViewModel.LoadDailyTrendCommand.Execute(null);

        Loaded += OnLoaded;

        ShowSection(HomeTabRoot, MenuHome);
    }

    private static string GetDatabasePath()
    {
        var app = (App)Application.Current;
        return Path.Combine(app.DataDirectory, app.Settings.LogDirectory, "resourcemonitor.db");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var chartHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "chart.html")).AbsoluteUri;

        await HomeLiveChartWebView.EnsureCoreWebView2Async();
        HomeLiveChartWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _homeWebViewReady = true;
            if (_pendingHomeJson is { } json)
            {
                _pendingHomeJson = null;
                _ = HomeLiveChartWebView.ExecuteScriptAsync($"renderSamples({json})");
            }
        };
        HomeLiveChartWebView.CoreWebView2.Navigate(chartHtmlUri);

        await LiveChartWebView.EnsureCoreWebView2Async();
        LiveChartWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _liveWebViewReady = true;
            if (_pendingLiveJson is { } json)
            {
                _pendingLiveJson = null;
                _ = LiveChartWebView.ExecuteScriptAsync($"renderSamples({json})");
            }
        };
        LiveChartWebView.CoreWebView2.Navigate(chartHtmlUri);

        await TrendChartWebView.EnsureCoreWebView2Async();
        TrendChartWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _trendWebViewReady = true;
            if (_pendingTrendJson is { } json)
            {
                _pendingTrendJson = null;
                _ = TrendChartWebView.ExecuteScriptAsync($"renderDailyTrend({json})");
            }
        };
        TrendChartWebView.CoreWebView2.Navigate(chartHtmlUri);
    }

    private bool _reportWebViewInitStarted;
    private bool _helpWebViewInitStarted;

    private UIElement? _currentSection;

    // Esconde todos os roots de conteúdo e mostra só o passado; marca qual item do menu
    // principal fica destacado (Tag="Active", ver MainMenuItemStyle em MainWindow.xaml).
    // Também liga/desliga o LiveMonitorService: só Home e Gráficos mostram o gráfico ao vivo,
    // então só incrementa/decrementa o contador de consumidores quando a troca realmente
    // entra ou sai de uma dessas duas seções (ver LiveMonitorService.AddConsumer/RemoveConsumer).
    private void ShowSection(UIElement section, MenuItem activeMenuItem)
    {
        foreach (var candidate in new UIElement[]
        {
            HomeTabRoot, MonitoringTabRoot, DataTabRoot, OffendersTabRoot,
            ChartLiveTabRoot, ChartTrendTabRoot, ChartEventosTabRoot, ReportTabRoot, HelpWebView,
        })
        {
            candidate.Visibility = ReferenceEquals(candidate, section) ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var item in new[] { MenuHome, MenuConfiguracoes, MenuDados, MenuOfensores, MenuGraficos, MenuRelatorios, MenuAjuda })
        {
            item.Tag = ReferenceEquals(item, activeMenuItem) ? "Active" : null;
        }

        var wasLiveConsumer = IsLiveConsumingSection(_currentSection);
        var isLiveConsumer = IsLiveConsumingSection(section);
        if (wasLiveConsumer && !isLiveConsumer)
        {
            ((App)Application.Current).LiveMonitor.RemoveConsumer();
        }
        else if (!wasLiveConsumer && isLiveConsumer)
        {
            ((App)Application.Current).LiveMonitor.AddConsumer();
        }

        _currentSection = section;
    }

    // Só Home e Gráficos > Dados Correntes mostram o gráfico ao vivo — Tendência e Eventos de
    // Picos são sob demanda (botão Carregar), não precisam do LiveMonitorService rodando.
    private bool IsLiveConsumingSection(UIElement? section) =>
        ReferenceEquals(section, HomeTabRoot) || ReferenceEquals(section, ChartLiveTabRoot);

    private void OnMenuHomeClick(object sender, RoutedEventArgs e) => ShowSection(HomeTabRoot, MenuHome);

    private void OnMenuConfiguracoesClick(object sender, RoutedEventArgs e) => ShowSection(MonitoringTabRoot, MenuConfiguracoes);

    private void OnMenuDadosClick(object sender, RoutedEventArgs e) => ShowSection(DataTabRoot, MenuDados);

    private void OnMenuOfensoresClick(object sender, RoutedEventArgs e) => ShowSection(OffendersTabRoot, MenuOfensores);

    private void OnMenuGraficosDadosCorrentesClick(object sender, RoutedEventArgs e) => ShowSection(ChartLiveTabRoot, MenuGraficos);

    private void OnMenuGraficosTendenciaClick(object sender, RoutedEventArgs e) => ShowSection(ChartTrendTabRoot, MenuGraficos);

    // Iniciado só na primeira vez que a seção é aberta (mesmo padrão de Relatórios/Ajuda) —
    // também é chamado por "Ver gráfico" (aba Dados), que pode chegar aqui antes do usuário
    // nunca ter clicado no menu Gráficos.
    private async void OnMenuGraficosEventosClick(object sender, RoutedEventArgs e)
    {
        ShowSection(ChartEventosTabRoot, MenuGraficos);
        await EnsureEventsWebViewsInitializedAsync();
        await _chartViewModel.EnsureInitialPeriodAsync();
    }

    private bool _eventsWebViewInitStarted;

    private async Task EnsureEventsWebViewsInitializedAsync()
    {
        if (_eventsWebViewInitStarted)
        {
            return;
        }

        _eventsWebViewInitStarted = true;

        var chartHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "chart.html")).AbsoluteUri;

        await EventsChartWebView.EnsureCoreWebView2Async();
        EventsChartWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _eventsWebViewReady = true;
            if (_pendingEventsJson is { } json)
            {
                _pendingEventsJson = null;
                _ = EventsChartWebView.ExecuteScriptAsync($"renderTrendWithEvents({json})");
            }
        };
        // Clique na bolinha do marcador (chart.html) posta os dados do episódio de volta — abre
        // um popup com data/intervalo + processos daquele pico, sem gráfico embutido.
        EventsChartWebView.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            try
            {
                var messageJson = args.TryGetWebMessageAsString();
                using var document = JsonDocument.Parse(messageJson);
                var root = document.RootElement;
                if (root.TryGetProperty("alertEventId", out var idProperty) && idProperty.TryGetInt64(out var alertEventId) &&
                    root.TryGetProperty("metric", out var metricProperty) &&
                    root.TryGetProperty("timestamp", out var timestampProperty) && timestampProperty.GetString() is { } timestampText)
                {
                    var timestamp = DateTimeOffset.Parse(timestampText, null, System.Globalization.DateTimeStyles.RoundtripKind);
                    double? durationMinutes = root.TryGetProperty("durationMinutes", out var durationProperty) && durationProperty.ValueKind == JsonValueKind.Number
                        ? durationProperty.GetDouble()
                        : null;
                    var isInterrupted = root.TryGetProperty("isInterrupted", out var interruptedProperty) && interruptedProperty.ValueKind == JsonValueKind.True;

                    _chartViewModel.LoadForAlertEvent(alertEventId, metricProperty.GetString() ?? "", timestamp, durationMinutes, isInterrupted);
                    ShowEpisodeDetailPopup();
                }
            }
            catch (JsonException)
            {
                // Mensagem que não veio do marcador de eventos (ou formato inesperado); ignora.
            }
        };
        EventsChartWebView.CoreWebView2.Navigate(chartHtmlUri);
    }

    private EpisodeDetailWindow? _episodeDetailWindow;

    private void ShowEpisodeDetailPopup()
    {
        if (_episodeDetailWindow is null || !_episodeDetailWindow.IsVisible)
        {
            _episodeDetailWindow = new EpisodeDetailWindow { Owner = this, DataContext = _chartViewModel };
            _episodeDetailWindow.Show();
        }
        else
        {
            _episodeDetailWindow.Activate();
        }
    }

    // Iniciado só na primeira vez que a seção é aberta (não no Loaded, junto com os outros
    // WebView2) — inicializar vários WebView2 ao mesmo tempo numa seção ainda não visível
    // causava o conteúdo não renderizar até o usuário navegar por outra seção antes.
    private async void OnMenuRelatoriosGeralClick(object sender, RoutedEventArgs e)
    {
        ShowSection(ReportTabRoot, MenuRelatorios);

        if (_reportWebViewInitStarted)
        {
            return;
        }

        _reportWebViewInitStarted = true;

        var reportHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "report.html")).AbsoluteUri;

        await ReportWebView.EnsureCoreWebView2Async();
        ReportWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _reportWebViewReady = true;
            if (_pendingReportJson is { } json)
            {
                _pendingReportJson = null;
                _ = ReportWebView.ExecuteScriptAsync($"renderReport({json})");
            }
        };
        ReportWebView.CoreWebView2.Navigate(reportHtmlUri);
    }

    private async void OnMenuAjudaClick(object sender, RoutedEventArgs e)
    {
        ShowSection(HelpWebView, MenuAjuda);

        if (_helpWebViewInitStarted)
        {
            return;
        }

        _helpWebViewInitStarted = true;

        var helpHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "ajuda.html")).AbsoluteUri;
        await HelpWebView.EnsureCoreWebView2Async();
        HelpWebView.CoreWebView2.Navigate(helpHtmlUri);
    }

    private async void OnViewChartRequested(object? sender, (long AlertEventId, string Metric, DateTimeOffset Timestamp, double? DurationMinutes, bool IsInterrupted) e)
    {
        ShowSection(ChartEventosTabRoot, MenuGraficos);
        await EnsureEventsWebViewsInitializedAsync();
        await _chartViewModel.EnsureInitialPeriodAsync();
        _chartViewModel.LoadForAlertEvent(e.AlertEventId, e.Metric, e.Timestamp, e.DurationMinutes, e.IsInterrupted);
        ShowEpisodeDetailPopup();
    }

    private void OnHomeLiveSamplesReady(object? sender, string json)
    {
        if (_homeWebViewReady)
        {
            _ = HomeLiveChartWebView.ExecuteScriptAsync($"renderSamples({json})");
        }
        else
        {
            _pendingHomeJson = json;
        }
    }

    private void OnLiveSamplesReady(object? sender, string json)
    {
        if (_liveWebViewReady)
        {
            _ = LiveChartWebView.ExecuteScriptAsync($"renderSamples({json})");
        }
        else
        {
            _pendingLiveJson = json;
        }
    }

    private void OnTrendWithEventsReady(object? sender, string json)
    {
        if (_eventsWebViewReady)
        {
            _ = EventsChartWebView.ExecuteScriptAsync($"renderTrendWithEvents({json})");
        }
        else
        {
            _pendingEventsJson = json;
        }
    }

    private void OnDailyTrendReady(object? sender, string json)
    {
        if (_trendWebViewReady)
        {
            _ = TrendChartWebView.ExecuteScriptAsync($"renderDailyTrend({json})");
        }
        else
        {
            _pendingTrendJson = json;
        }
    }

    private void OnReportReady(object? sender, string json)
    {
        if (_reportWebViewReady)
        {
            _ = ReportWebView.ExecuteScriptAsync($"renderReport({json})");
        }
        else
        {
            _pendingReportJson = json;
        }
    }

    private void OnCreditHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var app = (App)Application.Current;
        if (!app.IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
