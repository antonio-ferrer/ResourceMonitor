using System.ComponentModel;
using System.IO;
using System.Windows;
using ResourceMonitor.Gui.ViewModels;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui;

public partial class MainWindow : Window
{
    private readonly MonitoringViewModel _monitoringViewModel;
    private readonly DataViewModel _dataViewModel;
    private readonly ChartViewModel _chartViewModel;

    private bool _liveWebViewReady;
    private string? _pendingLiveJson;

    private bool _peakWebViewReady;
    private string? _pendingPeakJson;

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;

        _monitoringViewModel = new MonitoringViewModel(app.MonitoringService, app.Settings, app.DataDirectory);
        _dataViewModel = new DataViewModel(app.MonitoringService, GetDatabasePath);
        _chartViewModel = new ChartViewModel(app.MonitoringService, GetDatabasePath);

        MonitoringTabRoot.DataContext = _monitoringViewModel;
        DataTabRoot.DataContext = _dataViewModel;
        ChartTabRoot.DataContext = _chartViewModel;

        _dataViewModel.ViewChartRequested += OnViewChartRequested;
        _chartViewModel.PeakSamplesReady += OnPeakSamplesReady;
        _chartViewModel.LiveSamplesReady += OnLiveSamplesReady;

        Loaded += OnLoaded;
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
