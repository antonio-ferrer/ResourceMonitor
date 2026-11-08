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
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ResourceMonitor.Gui.ViewModels;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Controls.MenuItem;

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

    private readonly List<WebView2> _initializedWebViews = new();

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;

        Title = $"ResourceMonitor - v{AppVersion.Current}";
        if (app.TraceLogger.IsEnabled)
        {
            Title += " - modo depuração";
        }

        _homeViewModel = new HomeViewModel(app.MonitoringService, () => app.Settings, app.LiveMonitor);
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
        IsVisibleChanged += (_, _) =>
        {
            UpdateLiveConsumer();
            UpdateWebViewsBackgroundState();
        };
        StateChanged += (_, _) =>
        {
            UpdateLiveConsumer();
            UpdateWebViewsBackgroundState();
        };

        ShowSection(HomeTabRoot, MenuHome);
    }

    private static string GetDatabasePath()
    {
        var app = (App)Application.Current;
        return Path.Combine(app.DataDirectory, app.Settings.LogDirectory, "resourcemonitor.db");
    }

    private static string ChartHtmlUri =>
        new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "chart.html")).AbsoluteUri;

    // Todo WebView2 da janela passa por aqui: ambiente compartilhado (pasta de dados em
    // %LOCALAPPDATA%, ver App.GetWebViewEnvironmentAsync) e registro pra
    // UpdateWebViewsBackgroundState conseguir suspender os que já existem.
    private async Task InitializeWebViewAsync(WebView2 webView)
    {
        var environment = await ((App)Application.Current).GetWebViewEnvironmentAsync();
        await webView.EnsureCoreWebView2Async(environment);
        _initializedWebViews.Add(webView);
    }

    // Só o gráfico da Home sobe junto com a janela — os outros WebView2 (cada um é um processo
    // renderer próprio, ~20–25 MB) são criados na primeira vez que a tela deles é aberta.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync(HomeLiveChartWebView);
        HomeLiveChartWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _homeWebViewReady = true;
            if (_pendingHomeJson is { } json)
            {
                _pendingHomeJson = null;
                _ = HomeLiveChartWebView.ExecuteScriptAsync($"renderSamples({json})");
            }
        };
        HomeLiveChartWebView.CoreWebView2.Navigate(ChartHtmlUri);
    }

    private bool _webViewsInBackground;

    // Janela fora da vista (bandeja ou minimizada): pede pros WebView2 já criados liberarem
    // memória. Escondida na bandeja, também suspende — TrySuspendAsync exige o controle
    // invisível, o que minimizado não é. Ao voltar, o WebView2 retoma sozinho quando fica
    // visível; aqui só devolve o nível de memória normal.
    private async void UpdateWebViewsBackgroundState()
    {
        var inBackground = !IsVisible || WindowState == WindowState.Minimized;
        if (inBackground == _webViewsInBackground)
        {
            return;
        }

        _webViewsInBackground = inBackground;
        var targetLevel = inBackground
            ? CoreWebView2MemoryUsageTargetLevel.Low
            : CoreWebView2MemoryUsageTargetLevel.Normal;
        foreach (var webView in _initializedWebViews)
        {
            webView.CoreWebView2.MemoryUsageTargetLevel = targetLevel;
        }

        if (IsVisible)
        {
            return;
        }

        // Deixa os próprios controles processarem que ficaram invisíveis antes de suspender.
        await Dispatcher.Yield(DispatcherPriority.Background);
        foreach (var webView in _initializedWebViews.ToList())
        {
            if (IsVisible)
            {
                return;
            }

            try
            {
                await webView.CoreWebView2.TrySuspendAsync();
            }
            catch (Exception)
            {
                // Suspender é só economia de memória — se o WebView2 recusar, segue normal.
            }
        }
    }

    private bool _reportWebViewInitStarted;
    private bool _helpWebViewInitStarted;

    private UIElement? _currentSection;

    // Esconde todos os roots de conteúdo e mostra só o passado; marca qual item do menu
    // principal fica destacado (Tag="Active", ver MainMenuItemStyle em MainWindow.xaml).
    // Também liga/desliga o monitor ao vivo (ver UpdateLiveConsumer).
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

        _currentSection = section;
        UpdateLiveConsumer();

        if (ReferenceEquals(section, HomeTabRoot))
        {
            _homeViewModel.RefreshConfigSummary();
        }
    }

    // Só Home e Gráficos > Dados Correntes mostram o gráfico ao vivo — Tendência e Eventos de
    // Picos são sob demanda (botão Carregar), não precisam do LiveMonitorService rodando.
    private bool IsLiveConsumingSection(UIElement? section) =>
        ReferenceEquals(section, HomeTabRoot) || ReferenceEquals(section, ChartLiveTabRoot);

    private bool _isLiveConsumer;

    // O monitor ao vivo (varredura de processos a cada 1s) só roda com alguém olhando: janela
    // visível, não minimizada, numa seção com gráfico ao vivo. Fechar a janela só a esconde na
    // bandeja (ver OnClosing) — sem isso a varredura seguia rodando pra sempre ali.
    private void UpdateLiveConsumer()
    {
        var shouldConsume = IsVisible
            && WindowState != WindowState.Minimized
            && IsLiveConsumingSection(_currentSection);

        if (shouldConsume == _isLiveConsumer)
        {
            return;
        }

        _isLiveConsumer = shouldConsume;
        var liveMonitor = ((App)Application.Current).LiveMonitor;
        if (shouldConsume)
        {
            liveMonitor.AddConsumer();
        }
        else
        {
            liveMonitor.RemoveConsumer();
        }
    }

    private void OnMenuHomeClick(object sender, RoutedEventArgs e) => ShowSection(HomeTabRoot, MenuHome);

    private void OnMenuConfiguracoesClick(object sender, RoutedEventArgs e) => ShowSection(MonitoringTabRoot, MenuConfiguracoes);

    private async void OnMenuDadosClick(object sender, RoutedEventArgs e)
    {
        ShowSection(TemplatesTabRoot, MenuDados);
        await _templatesViewModel.EnsureInitialPeriodAsync();
    }

    // Tab insere 4 espaços em vez de mover o foco pra fora do editor — sem isso digitar SQL
    // com indentação vira impossível.
    private void OnSqlEditorPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        SqlEditorTextBox.SelectedText = "    ";
        SqlEditorTextBox.CaretIndex = SqlEditorTextBox.SelectionStart + SqlEditorTextBox.SelectionLength;
        e.Handled = true;
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

    private bool _liveWebViewInitStarted;

    // Criado só na primeira vez que a tela abre, já visível (mesmo padrão de Eventos/Relatórios).
    // Amostras que chegarem antes ficam em _pendingLiveJson.
    private async void OnMenuGraficosDadosCorrentesClick(object sender, RoutedEventArgs e)
    {
        ShowSection(ChartLiveTabRoot, MenuGraficos);

        if (_liveWebViewInitStarted)
        {
            return;
        }

        _liveWebViewInitStarted = true;
        await InitializeWebViewAsync(LiveChartWebView);
        LiveChartWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _liveWebViewReady = true;
            if (_pendingLiveJson is { } json)
            {
                _pendingLiveJson = null;
                _ = LiveChartWebView.ExecuteScriptAsync($"renderSamples({json})");
            }
        };
        LiveChartWebView.CoreWebView2.Navigate(ChartHtmlUri);
    }

    private bool _trendWebViewInitStarted;

    // Idem Dados Correntes. A tendência já é carregada no construtor e espera em _pendingTrendJson.
    private async void OnMenuGraficosTendenciaClick(object sender, RoutedEventArgs e)
    {
        ShowSection(ChartTrendTabRoot, MenuGraficos);

        if (_trendWebViewInitStarted)
        {
            return;
        }

        _trendWebViewInitStarted = true;
        await InitializeWebViewAsync(TrendChartWebView);
        TrendChartWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _trendWebViewReady = true;
            if (_pendingTrendJson is { } json)
            {
                _pendingTrendJson = null;
                _ = TrendChartWebView.ExecuteScriptAsync($"renderDailyTrend({json})");
            }
        };
        TrendChartWebView.CoreWebView2.Navigate(ChartHtmlUri);
    }

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

        await InitializeWebViewAsync(EventsChartWebView);
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
        EventsChartWebView.CoreWebView2.Navigate(ChartHtmlUri);
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

    // Iniciado só na primeira vez que a seção é aberta (não no Loaded) — inicializar vários
    // WebView2 ao mesmo tempo numa seção ainda não visível causava o conteúdo não renderizar
    // até o usuário navegar por outra seção antes. Aqui a gente espera de verdade o
    // NavigationCompleted (TaskCompletionSource) — é o que permite já disparar "Gerar
    // relatório" com o período padrão (última semana) assim que abre, em vez de esperar o
    // usuário clicar. Cursor de espera cobre a inicialização do WebView2 (a parte que pode
    // demorar de verdade em máquinas mais lentas).
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

            await InitializeWebViewAsync(ReportWebView);
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
        await InitializeWebViewAsync(HelpWebView);
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
