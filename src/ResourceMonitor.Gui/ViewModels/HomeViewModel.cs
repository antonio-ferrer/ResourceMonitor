using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Configuration;
using ResourceMonitor.Gui;
using ResourceMonitor.Gui.Controls;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Updates;

namespace ResourceMonitor.Gui.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private readonly MonitoringService _monitoringService;

    // Lido a cada atualização, não guardado: gravar as Configurações troca o objeto inteiro em
    // App.Settings, e uma referência capturada no construtor ficaria mostrando o antigo.
    private readonly Func<MonitorSettings> _getSettings;

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

    // Gráfico ao vivo da Home (Controls/LineChart), por binding.
    [ObservableProperty] private ChartData? liveChart;

    public HomeViewModel(MonitoringService monitoringService, Func<MonitorSettings> getSettings, LiveMonitorService liveMonitor)
    {
        _monitoringService = monitoringService;
        _getSettings = getSettings;

        liveMonitor.SnapshotUpdated += OnLiveSnapshotUpdated;

        // Pode disparar da thread do loop de monitoramento (quando ele para sozinho por falha).
        _monitoringService.RunningStateChanged += (_, _) =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(RefreshConfigSummary);

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

    // Chamado também toda vez que a Home é mostrada (MainWindow.ShowSection) — cobre mudanças
    // feitas em Configurações (gravadas ao sair da tela, "Iniciar com o Windows") sem evento próprio.
    public void RefreshConfigSummary()
    {
        var settings = _getSettings();

        MonitoringActive = _monitoringService.IsRunning;
        StartWithWindowsActive = AutoStartManager.IsEnabled();
        ExcludedProcessesActive = settings.ExcludedProcesses.Count > 0;
        CustomDiskThresholdsActive = settings.Thresholds.DiskFreeThresholds
            .Any(t => Math.Abs(t.MinFreePercent - DiskThreshold.DefaultMinFreePercent) > 0.01);

        ThresholdsSummary = $"{settings.Thresholds.CpuPercent:N0}% · {settings.Thresholds.RamPercent:N0}% · {settings.Thresholds.DiskIoPercent:N0}%";
        SampleIntervalSummary = $"{settings.SampleIntervalSeconds}s";
    }

    // InvokeAsync (não Invoke): o tick do monitor ao vivo não fica preso esperando a UI.
    private void OnLiveSnapshotUpdated(object? sender, LiveSnapshot snapshot)
    {
        var chart = ChartData.FromLiveSamples(snapshot.Samples);

        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ReplaceAll(TopByCpu, snapshot.TopByCpu);
            ReplaceAll(TopByRam, snapshot.TopByRam);
            ReplaceAll(TopByIo, snapshot.TopByIo);

            LiveChart = chart;
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
