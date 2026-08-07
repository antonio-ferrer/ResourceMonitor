using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Configuration;
using ResourceMonitor.Gui;
using ResourceMonitor.Gui.Converters;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Updates;

namespace ResourceMonitor.Gui.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly MonitoringService _monitoringService;
    private readonly MonitorSettings _settings;

    [ObservableProperty] private HardwareInfo hardware = HardwareInfoReader.Capture();

    [ObservableProperty] private bool monitoringActive;
    [ObservableProperty] private bool startWithWindowsActive;
    [ObservableProperty] private bool excludedProcessesActive;
    [ObservableProperty] private bool customDiskThresholdsActive;
    [ObservableProperty] private string thresholdsSummary = string.Empty;
    [ObservableProperty] private string sampleIntervalSummary = string.Empty;

    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private string updateAvailableMessage = string.Empty;
    [ObservableProperty] private string? updateReleaseUrl;

    // Top 5 por métrica, em abas separadas pra não misturar — subproduto do ResourceSampler
    // que já roda pro gráfico, sem nova varredura de processos.
    public ObservableCollection<GroupedProcessUsage> TopByCpu { get; } = new();
    public ObservableCollection<GroupedProcessUsage> TopByRam { get; } = new();
    public ObservableCollection<GroupedProcessUsage> TopByIo { get; } = new();

    public event EventHandler<string>? LiveSamplesReady;

    public HomeViewModel(MonitoringService monitoringService, MonitorSettings settings, LiveMonitorService liveMonitor)
    {
        _monitoringService = monitoringService;
        _settings = settings;

        liveMonitor.SnapshotUpdated += OnLiveSnapshotUpdated;
        _monitoringService.RunningStateChanged += (_, _) => RefreshConfigSummary();

        RefreshConfigSummary();

        // Uma vez por execução — o construtor só roda uma vez, mesmo ciclo de vida da
        // janela, então não precisa de flag extra pra evitar checagem repetida.
        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        var result = await UpdateChecker.CheckAsync(AppVersion.Current);
        if (!result.IsUpdateAvailable)
        {
            return;
        }

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateAvailableMessage = $"Uma nova versão ({result.LatestVersion}) está disponível — você está usando v{AppVersion.Current}.";
            UpdateReleaseUrl = result.ReleaseUrl;
            UpdateAvailable = true;
        });
    }

    [RelayCommand]
    private void Refresh()
    {
        Hardware = HardwareInfoReader.Capture();
        RefreshConfigSummary();
    }

    private void RefreshConfigSummary()
    {
        MonitoringActive = _monitoringService.IsRunning;
        StartWithWindowsActive = AutoStartManager.IsEnabled();
        ExcludedProcessesActive = _settings.ExcludedProcesses.Count > 0;
        CustomDiskThresholdsActive = _settings.Thresholds.DiskFreeThresholds
            .Any(t => Math.Abs(t.MinFreePercent - DiskThreshold.DefaultMinFreePercent) > 0.01);

        ThresholdsSummary = $"{_settings.Thresholds.CpuPercent:N0}% · {_settings.Thresholds.RamPercent:N0}% · {_settings.Thresholds.DiskIoPercent:N0}%";
        SampleIntervalSummary = $"{_settings.SampleIntervalSeconds}s";
    }

    private void OnLiveSnapshotUpdated(object? sender, LiveSnapshot snapshot)
    {
        var json = ChartJsonFormatter.ToChartJson(snapshot.Samples);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            ReplaceAll(TopByCpu, snapshot.TopByCpu);
            ReplaceAll(TopByRam, snapshot.TopByRam);
            ReplaceAll(TopByIo, snapshot.TopByIo);

            LiveSamplesReady?.Invoke(this, json);
        });
    }

    private static void ReplaceAll(ObservableCollection<GroupedProcessUsage> target, IReadOnlyList<GroupedProcessUsage> source)
    {
        target.Clear();
        foreach (var process in source)
        {
            target.Add(process);
        }
    }
}
