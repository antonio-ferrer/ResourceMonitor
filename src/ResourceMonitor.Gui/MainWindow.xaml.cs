using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Navigation;
using ResourceMonitor.Gui.ViewModels;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui;

public partial class MainWindow : Window
{
    private readonly HomeViewModel _homeViewModel;
    private readonly MonitoringViewModel _monitoringViewModel;
    private readonly TemplatesViewModel _templatesViewModel;
    private readonly ChartViewModel _chartViewModel;
    private readonly ReportViewModel _reportViewModel;

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

    private bool _sqlEditorWebViewReady;
    private string? _pendingSqlEditorText;

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;

        if (app.TraceLogger.IsEnabled)
        {
            Title += " - modo depuração";
        }

        _homeViewModel = new HomeViewModel(app.MonitoringService, app.Settings, app.LiveMonitor);
        _monitoringViewModel = new MonitoringViewModel(app.MonitoringService, app.Settings, app.DataDirectory, app.TrayNotifier, GetDatabasePath);
        _templatesViewModel = new TemplatesViewModel(GetDatabasePath, app.TemplateQueries);
        _chartViewModel = new ChartViewModel(app.LiveMonitor, GetDatabasePath, app.AlertEventQueries);
        _reportViewModel = new ReportViewModel(GetDatabasePath, app.AlertEventQueries);

        HomeTabRoot.DataContext = _homeViewModel;
        MonitoringTabRoot.DataContext = _monitoringViewModel;
        TemplatesTabRoot.DataContext = _templatesViewModel;
        ChartLiveTabRoot.DataContext = _chartViewModel;
        ChartTrendTabRoot.DataContext = _chartViewModel;
        ChartEventosTabRoot.DataContext = _chartViewModel;
        ReportTabRoot.DataContext = _reportViewModel;

        _templatesViewModel.GetEditorTextAsync = GetSqlEditorTextAsync;
        _templatesViewModel.SetEditorText = SetSqlEditorText;
        _templatesViewModel.ResultReady += OnTemplateResultReady;
        _templatesViewModel.PropertyChanged += OnTemplatesViewModelPropertyChanged;

        _homeViewModel.LiveSamplesReady += OnHomeLiveSamplesReady;
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
            HomeTabRoot, MonitoringTabRoot, TemplatesTabRoot,
            ChartLiveTabRoot, ChartTrendTabRoot, ChartEventosTabRoot, ReportTabRoot, HelpWebView,
        })
        {
            candidate.Visibility = ReferenceEquals(candidate, section) ? Visibility.Visible : Visibility.Collapsed;
        }

        foreach (var item in new[] { MenuHome, MenuConfiguracoes, MenuDados, MenuGraficos, MenuRelatorios, MenuAjuda })
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

    private async void OnMenuDadosClick(object sender, RoutedEventArgs e)
    {
        ShowSection(TemplatesTabRoot, MenuDados);
        await EnsureSqlEditorWebViewInitializedAsync();
        await _templatesViewModel.EnsureInitialPeriodAsync();
    }

    private bool _sqlEditorWebViewInitStarted;

    // Mesmo padrão lazy-init de EventsChartWebView/ReportWebView — inicializado só na
    // primeira vez que a tela é aberta. Diferente dos outros: aqui a gente REALMENTE espera
    // o NavigationCompleted (via TaskCompletionSource) antes de retornar — Executar logo em
    // seguida (EnsureInitialPeriodAsync chama ExecutarAsync) precisa ler o texto do editor
    // via getValue(), que só existe depois que o setValue(comando inicial) já rodou.
    private async Task EnsureSqlEditorWebViewInitializedAsync()
    {
        if (_sqlEditorWebViewInitStarted)
        {
            return;
        }

        _sqlEditorWebViewInitStarted = true;

        var editorHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "sql-editor.html")).AbsoluteUri;
        var navigationCompleted = new TaskCompletionSource();

        await SqlEditorWebView.EnsureCoreWebView2Async();
        SqlEditorWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _sqlEditorWebViewReady = true;
            navigationCompleted.TrySetResult();
        };
        SqlEditorWebView.CoreWebView2.Navigate(editorHtmlUri);

        await navigationCompleted.Task;

        var initialText = _pendingSqlEditorText ?? _templatesViewModel.SelectedTemplate?.Command ?? string.Empty;
        _pendingSqlEditorText = null;
        await SqlEditorWebView.ExecuteScriptAsync($"setValue({JsonSerializer.Serialize(initialText)})");
    }

    // RowDefinition não herda DataContext de forma confiável no WPF ({Binding} direto na
    // altura da linha simplesmente não reagia à troca do toggle) — então o toggle do editor
    // (botão "Editor") ajusta a altura das linhas diretamente aqui, via PropertyChanged do
    // ViewModel, em vez de um binding de XAML.
    private void OnTemplatesViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TemplatesViewModel.IsEditorVisible))
        {
            return;
        }

        var visible = _templatesViewModel.IsEditorVisible;
        EditorRow.Height = new GridLength(visible ? 180 : 0);
        EditorSplitterRow.Height = new GridLength(visible ? 6 : 0);
    }

    private void SetSqlEditorText(string text)
    {
        if (_sqlEditorWebViewReady)
        {
            _ = SqlEditorWebView.ExecuteScriptAsync($"setValue({JsonSerializer.Serialize(text)})");
        }
        else
        {
            _pendingSqlEditorText = text;
        }
    }

    private async Task<string> GetSqlEditorTextAsync()
    {
        if (!_sqlEditorWebViewReady)
        {
            return string.Empty;
        }

        var json = await SqlEditorWebView.ExecuteScriptAsync("getValue()");
        return JsonSerializer.Deserialize<string>(json) ?? string.Empty;
    }

    // Colunas não são conhecidas em tempo de compilação (resultado de SQL arbitrário) —
    // primeiro precedente do projeto com DataGridTextColumn construída em code-behind em
    // vez de XAML. Binding por índice — cada linha é um IReadOnlyList<string?> (ver
    // TemplateQueries.ExecuteReadOnly), que expõe indexador compatível com o binding do WPF.
    private void OnTemplateResultReady(object? sender, QueryResult result)
    {
        TemplateResultsGrid.Columns.Clear();
        for (var i = 0; i < result.Columns.Count; i++)
        {
            TemplateResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i],
                Binding = new System.Windows.Data.Binding($"[{i}]"),
            });
        }

        TemplateResultsGrid.ItemsSource = result.Rows;
    }

    // Caso especial: se o resultado tiver StartEventId+Metric (como o template "Base de
    // picos" sempre tem), duplo-clique na linha abre o mesmo popup de detalhe do pico que
    // "Ver gráfico" abria na antiga aba Dados — reaproveita EpisodeDetailWindow.
    private async void OnTemplateResultsGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TemplateResultsGrid.SelectedItem is not IReadOnlyList<string?> row)
        {
            return;
        }

        var columns = TemplateResultsGrid.Columns;
        var startEventIdIndex = FindColumnIndex(columns, "StartEventId");
        var metricIndex = FindColumnIndex(columns, "Metric");
        if (startEventIdIndex < 0 || metricIndex < 0)
        {
            return;
        }

        if (!long.TryParse(row[startEventIdIndex], out var alertEventId))
        {
            return;
        }

        var metric = row[metricIndex] ?? string.Empty;

        var timestampIndex = FindColumnIndex(columns, "StartTimestamp");
        var timestamp = timestampIndex >= 0 && row[timestampIndex] is { } timestampText
            && DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTimestamp)
            ? parsedTimestamp
            : DateTimeOffset.UtcNow;

        var durationIndex = FindColumnIndex(columns, "DurationMinutes");
        double? durationMinutes = durationIndex >= 0 && double.TryParse(row[durationIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration)
            ? parsedDuration
            : null;

        var interruptedIndex = FindColumnIndex(columns, "Interrupted");
        var isInterrupted = interruptedIndex >= 0 && row[interruptedIndex] is "1" or "True" or "true";

        ShowSection(ChartEventosTabRoot, MenuGraficos);
        await EnsureEventsWebViewsInitializedAsync();
        await _chartViewModel.EnsureInitialPeriodAsync();
        _chartViewModel.LoadForAlertEvent(alertEventId, metric, timestamp, durationMinutes, isInterrupted);
        ShowEpisodeDetailPopup();
    }

    private static int FindColumnIndex(IEnumerable<DataGridColumn> columns, string name)
    {
        var index = 0;
        foreach (var column in columns)
        {
            if (column.Header is string header && string.Equals(header, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

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
    // Mesmo padrão de espera real (TaskCompletionSource) de EnsureSqlEditorWebViewInitializedAsync
    // — aqui é o que permite já disparar "Gerar relatório" com o período padrão (última semana)
    // assim que abre, em vez de esperar o usuário clicar. Cursor de espera cobre a inicialização
    // do WebView2 (a parte que pode demorar de verdade em máquinas mais lentas).
    private async void OnMenuRelatoriosClick(object sender, RoutedEventArgs e)
    {
        ShowSection(ReportTabRoot, MenuRelatorios);

        if (_reportWebViewInitStarted)
        {
            return;
        }

        _reportWebViewInitStarted = true;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var reportHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "report.html")).AbsoluteUri;
            var navigationCompleted = new TaskCompletionSource();

            await ReportWebView.EnsureCoreWebView2Async();
            ReportWebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _reportWebViewReady = true;
                navigationCompleted.TrySetResult();
            };
            ReportWebView.CoreWebView2.Navigate(reportHtmlUri);
            await navigationCompleted.Task;

            _reportViewModel.GerarRelatorioCommand.Execute(null);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
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
