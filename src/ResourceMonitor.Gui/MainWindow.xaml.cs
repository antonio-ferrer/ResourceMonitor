using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using ResourceMonitor.Gui.ViewModels;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui;

public partial class MainWindow : Window
{
    private readonly MonitoringViewModel _monitoringViewModel;
    private readonly DataViewModel _dataViewModel;
    private readonly ChartViewModel _chartViewModel;
    private readonly ReportViewModel _reportViewModel;

    private bool _liveWebViewReady;
    private string? _pendingLiveJson;

    private bool _peakWebViewReady;
    private string? _pendingPeakJson;

    private bool _reportWebViewReady;
    private string? _pendingReportJson;

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;

        if (app.TraceLogger.IsEnabled)
        {
            Title += " - modo depuração";
        }

        _monitoringViewModel = new MonitoringViewModel(app.MonitoringService, app.Settings, app.DataDirectory, app.TrayNotifier);
        _dataViewModel = new DataViewModel(app.MonitoringService, GetDatabasePath, app.AlertEventQueries, app.TraceLogger);
        _chartViewModel = new ChartViewModel(app.MonitoringService, GetDatabasePath, app.AlertEventQueries);
        _reportViewModel = new ReportViewModel(GetDatabasePath, app.AlertEventQueries);

        MonitoringTabRoot.DataContext = _monitoringViewModel;
        DataTabRoot.DataContext = _dataViewModel;
        ChartTabRoot.DataContext = _chartViewModel;
        ReportTabRoot.DataContext = _reportViewModel;

        _dataViewModel.ViewChartRequested += OnViewChartRequested;
        _chartViewModel.PeakSamplesReady += OnPeakSamplesReady;
        _chartViewModel.LiveSamplesReady += OnLiveSamplesReady;
        _reportViewModel.ReportReady += OnReportReady;

        Loaded += OnLoaded;
        RootTabControl.SelectionChanged += OnTabControlSelectionChanged;
    }

    private static string GetDatabasePath()
    {
        var app = (App)Application.Current;
        return Path.Combine(app.DataDirectory, app.Settings.LogDirectory, "resourcemonitor.db");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var chartHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "chart.html")).AbsoluteUri;

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

        await PeakChartWebView.EnsureCoreWebView2Async();
        PeakChartWebView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _peakWebViewReady = true;
            if (_pendingPeakJson is { } json)
            {
                _pendingPeakJson = null;
                _ = PeakChartWebView.ExecuteScriptAsync($"renderSamples({json})");
            }
        };
        PeakChartWebView.CoreWebView2.Navigate(chartHtmlUri);
    }

    private bool _reportWebViewInitStarted;

    // Iniciado só quando a aba Relatórios é selecionada pela primeira vez (não no Loaded, junto
    // com os outros WebView2) — inicializar 3 WebView2 ao mesmo tempo numa aba ainda não
    // visível causava o relatório não renderizar até o usuário navegar por outra aba antes.
    private async void OnTabControlSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_reportWebViewInitStarted || RootTabControl.SelectedItem != ReportTab)
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

    private void OnViewChartRequested(object? sender, long alertEventId)
    {
        RootTabControl.SelectedIndex = 2;
        _chartViewModel.LoadForAlertEvent(alertEventId);
    }

    private void OnPeakSamplesReady(object? sender, string json)
    {
        if (_peakWebViewReady)
        {
            _ = PeakChartWebView.ExecuteScriptAsync($"renderSamples({json})");
        }
        else
        {
            _pendingPeakJson = json;
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
