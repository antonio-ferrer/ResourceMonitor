using System.ComponentModel;
using System.Windows;
using ResourceMonitor.Gui.Services;
using ResourceMonitor.Gui.ViewModels;
using ResourceMonitor.Gui.Views;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Storage;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ResourceMonitor.Gui;

// Moldura da janela: menu, troca de telas (Views/), monitor ao vivo só com alguém olhando,
// WebView2 em segundo plano quando a janela sai da vista, e o popup de detalhe de pico.
public partial class MainWindow : Window
{
    private readonly HomeViewModel _homeViewModel;
    private readonly MonitoringViewModel _monitoringViewModel;
    private readonly TemplatesViewModel _templatesViewModel;
    private readonly ChartViewModel _chartViewModel;
    private readonly LiveMonitorService _liveMonitor;
    private readonly WebViewHost _webViewHost;

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
        _liveMonitor = liveMonitor;
        _webViewHost = webViewHost;

        Title = $"ResourceMonitor - v{AppVersion.Current}";
        if (debugMode)
        {
            Title += " - modo depuração";
        }

        HomeTabRoot.DataContext = homeViewModel;
        MonitoringTabRoot.DataContext = monitoringViewModel;
        TemplatesTabRoot.DataContext = templatesViewModel;
        ChartLiveTabRoot.DataContext = chartViewModel;
        ChartTrendTabRoot.DataContext = chartViewModel;
        ChartEventosTabRoot.DataContext = chartViewModel;
        ReportTabRoot.DataContext = reportViewModel;

        HomeTabRoot.SettingsRequested += (_, _) => ShowSection(MonitoringTabRoot, MenuConfiguracoes);
        TemplatesTabRoot.AlertEventRequested += OnAlertEventRequested;
        ChartEventosTabRoot.EpisodeRequested += OnEpisodeRequested;

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

    private UIElement? _currentSection;

    // Esconde todas as telas e mostra só a passada; marca qual item do menu principal fica
    // destacado (Tag="Active", ver MainMenuItemStyle em Themes/Styles.xaml). Também liga/desliga
    // o monitor ao vivo (ver UpdateLiveConsumer).
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
            ChartLiveTabRoot, ChartTrendTabRoot, ChartEventosTabRoot, ReportTabRoot, HelpTabRoot,
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
    // Picos são sob demanda, não precisam do LiveMonitorService rodando.
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

    private async void OnMenuRelatoriosClick(object sender, RoutedEventArgs e)
    {
        ShowSection(ReportTabRoot, MenuRelatorios);
        await ReportTabRoot.EnsureInitializedAsync(_webViewHost);
    }

    private async void OnMenuAjudaClick(object sender, RoutedEventArgs e)
    {
        ShowSection(HelpTabRoot, MenuAjuda);
        await HelpTabRoot.EnsureInitializedAsync(_webViewHost);
    }

    // Duplo-clique numa linha de pico em Templates: vai pra Eventos de Picos e abre o detalhe.
    private async void OnAlertEventRequested(object? sender, AlertEventRequest request)
    {
        ShowSection(ChartEventosTabRoot, MenuGraficos);
        await _chartViewModel.EnsureInitialPeriodAsync();
        _chartViewModel.LoadForAlertEvent(
            request.AlertEventId, request.Metric, request.Timestamp, request.DurationMinutes, request.IsInterrupted);
        ShowEpisodeDetailPopup();
    }

    // Clique na bolinha de um marcador: popup com data/intervalo + processos daquele pico.
    private void OnEpisodeRequested(object? sender, AlertEpisodeRow episode)
    {
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
