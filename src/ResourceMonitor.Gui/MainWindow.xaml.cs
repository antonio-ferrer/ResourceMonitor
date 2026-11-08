using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Navigation;
using ResourceMonitor.Gui.Controls;
using ResourceMonitor.Gui.Services;
using ResourceMonitor.Gui.ViewModels;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Storage;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ResourceMonitor.Gui;

public partial class MainWindow : Window
{
    private readonly HomeViewModel _homeViewModel;
    private readonly MonitoringViewModel _monitoringViewModel;
    private readonly TemplatesViewModel _templatesViewModel;
    private readonly ChartViewModel _chartViewModel;
    private readonly ReportViewModel _reportViewModel;
    private readonly LiveMonitorService _liveMonitor;
    private readonly WebViewHost _webViewHost;

    private bool _reportWebViewReady;
    private string? _pendingReportJson;

    // Tudo chega montado pelo App (raiz de composição).
    public MainWindow(
        HomeViewModel homeViewModel,
        MonitoringViewModel monitoringViewModel,
        TemplatesViewModel templatesViewModel,
        ChartViewModel chartViewModel,
        ReportViewModel reportViewModel,
        LiveMonitorService liveMonitor,
        WebViewHost webViewHost,
        bool debugMode)
    {
        InitializeComponent();

        _homeViewModel = homeViewModel;
        _monitoringViewModel = monitoringViewModel;
        _templatesViewModel = templatesViewModel;
        _chartViewModel = chartViewModel;
        _reportViewModel = reportViewModel;
        _liveMonitor = liveMonitor;
        _webViewHost = webViewHost;

        Title = $"ResourceMonitor - v{AppVersion.Current}";
        if (debugMode)
        {
            Title += " - modo depuração";
        }

        HomeTabRoot.DataContext = _homeViewModel;
        MonitoringTabRoot.DataContext = _monitoringViewModel;
        TemplatesTabRoot.DataContext = _templatesViewModel;
        ChartLiveTabRoot.DataContext = _chartViewModel;
        ChartTrendTabRoot.DataContext = _chartViewModel;
        ChartEventosTabRoot.DataContext = _chartViewModel;
        ReportTabRoot.DataContext = _reportViewModel;

        _templatesViewModel.ResultReady += OnTemplateResultReady;
        _templatesViewModel.PropertyChanged += OnTemplatesViewModelPropertyChanged;

        _reportViewModel.ReportReady += OnReportReady;

        _chartViewModel.LoadDailyTrendCommand.Execute(null);

        IsVisibleChanged += (_, _) =>
        {
            UpdateLiveConsumer();
            _ = _webViewHost.UpdateBackgroundStateAsync(this);
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                SaveSettingsIfChanged();
            }

            UpdateLiveConsumer();
            _ = _webViewHost.UpdateBackgroundStateAsync(this);
        };

        ShowSection(HomeTabRoot, MenuHome);
    }

    // Ligado pelo App ao sair de verdade (menu Sair da bandeja): aí fechar a janela fecha mesmo,
    // em vez de só esconder na bandeja.
    public bool IsExiting { get; set; }

    private bool _reportWebViewInitStarted;
    private bool _helpWebViewInitStarted;

    private UIElement? _currentSection;

    // Esconde todos os roots de conteúdo e mostra só o passado; marca qual item do menu
    // principal fica destacado (Tag="Active", ver MainMenuItemStyle em MainWindow.xaml).
    // Também liga/desliga o monitor ao vivo (ver UpdateLiveConsumer).
    private void ShowSection(UIElement section, MenuItem activeMenuItem)
    {
        // Saindo de Configurações: grava o que foi editado (não há botão Salvar).
        if (ReferenceEquals(_currentSection, MonitoringTabRoot) && !ReferenceEquals(section, MonitoringTabRoot))
        {
            SaveSettingsIfChanged();
        }

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
        if (shouldConsume)
        {
            _liveMonitor.AddConsumer();
        }
        else
        {
            _liveMonitor.RemoveConsumer();
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

    private void OnMenuGraficosDadosCorrentesClick(object sender, RoutedEventArgs e) =>
        ShowSection(ChartLiveTabRoot, MenuGraficos);

    // A tendência já é carregada no construtor.
    private void OnMenuGraficosTendenciaClick(object sender, RoutedEventArgs e) =>
        ShowSection(ChartTrendTabRoot, MenuGraficos);

    private async void OnMenuGraficosEventosClick(object sender, RoutedEventArgs e)
    {
        ShowSection(ChartEventosTabRoot, MenuGraficos);
        await _chartViewModel.EnsureInitialPeriodAsync();
    }

    // Clique na bolinha de um marcador: popup com data/intervalo + processos daquele pico.
    private void OnEventsChartMarkerClicked(object? sender, ChartMarker marker)
    {
        if (marker.Tag is not AlertEpisodeRow episode)
        {
            return;
        }

        _chartViewModel.LoadForAlertEpisode(episode);
        ShowEpisodeDetailPopup();
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

            await _webViewHost.InitializeAsync(ReportWebView);
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
        await _webViewHost.InitializeAsync(HelpWebView);
        HelpWebView.CoreWebView2.Navigate(helpHtmlUri);
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

    // Chamado também pelo App ao sair e quando o Windows encerra a sessão.
    public void SaveSettingsIfChanged() => _monitoringViewModel.SaveIfChanged();

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveSettingsIfChanged();
        if (!IsExiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
