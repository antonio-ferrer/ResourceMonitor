using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Gui;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private readonly MonitoringService _monitoringService;
    private readonly string _dataDirectory;

    [ObservableProperty] private int sampleIntervalSeconds;
    [ObservableProperty] private int consecutiveBreachesToAlert;
    [ObservableProperty] private int consecutiveRecoveriesToClear;
    [ObservableProperty] private int topProcessCount;
    [ObservableProperty] private int preEventSeconds;
    [ObservableProperty] private int postEventSeconds;
    [ObservableProperty] private double cpuPercent;
    [ObservableProperty] private double ramPercent;
    [ObservableProperty] private double diskFreePercentMin;
    [ObservableProperty] private double diskIoPercent;

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string statusText = "Parado.";
    [ObservableProperty] private string lastSampleText = "Sem amostras ainda.";
    [ObservableProperty] private bool startWithWindows;

    public bool CanEditSettings => !IsRunning;

    public MonitoringViewModel(MonitoringService monitoringService, MonitorSettings initialSettings, string dataDirectory)
    {
        _monitoringService = monitoringService;
        _dataDirectory = dataDirectory;

        LoadFrom(initialSettings);
        IsRunning = _monitoringService.IsRunning;
        StartWithWindows = AutoStartManager.IsEnabled();

        _monitoringService.SampleCollected += OnSampleCollected;
        _monitoringService.AlertRaised += OnAlertRaised;
        _monitoringService.Faulted += OnFaulted;
    }

    private void LoadFrom(MonitorSettings settings)
    {
        SampleIntervalSeconds = settings.SampleIntervalSeconds;
        ConsecutiveBreachesToAlert = settings.ConsecutiveBreachesToAlert;
        ConsecutiveRecoveriesToClear = settings.ConsecutiveRecoveriesToClear;
        TopProcessCount = settings.TopProcessCount;
        PreEventSeconds = settings.PreEventSeconds;
        PostEventSeconds = settings.PostEventSeconds;
        CpuPercent = settings.Thresholds.CpuPercent;
        RamPercent = settings.Thresholds.RamPercent;
        DiskFreePercentMin = settings.Thresholds.DiskFreePercentMin;
        DiskIoPercent = settings.Thresholds.DiskIoPercent;
    }

    private MonitorSettings BuildSettings() => new()
    {
        SampleIntervalSeconds = SampleIntervalSeconds,
        ConsecutiveBreachesToAlert = ConsecutiveBreachesToAlert,
        ConsecutiveRecoveriesToClear = ConsecutiveRecoveriesToClear,
        TopProcessCount = TopProcessCount,
        PreEventSeconds = PreEventSeconds,
        PostEventSeconds = PostEventSeconds,
        Thresholds = new ThresholdSettings
        {
            CpuPercent = CpuPercent,
            RamPercent = RamPercent,
            DiskFreePercentMin = DiskFreePercentMin,
            DiskIoPercent = DiskIoPercent,
        },
    };

    [RelayCommand]
    private void Save()
    {
        var settings = BuildSettings();
        AppSettingsStore.Save(settings);
        ((App)Application.Current).Settings = settings;
        StatusText = "Configurações salvas.";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void RestoreDefaults()
    {
        LoadFrom(new MonitorSettings());
        StatusText = "Valores padrão restaurados nos campos. Clique Salvar pra persistir.";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        var settings = BuildSettings();
        _monitoringService.Start(settings, _dataDirectory);
        SetRunning(true);
        StatusText = "Monitorando...";
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        await _monitoringService.StopAsync();
        SetRunning(false);
        StatusText = "Parado.";
    }

    private bool CanStop() => IsRunning;

    private void SetRunning(bool running)
    {
        IsRunning = running;
        OnPropertyChanged(nameof(CanEditSettings));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestoreDefaultsCommand.NotifyCanExecuteChanged();
    }

    private void OnSampleCollected(object? sender, ResourceSample sample)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LastSampleText =
                $"[{sample.Timestamp:HH:mm:ss}] CPU {sample.CpuAdjustedPercent:F1}% | RAM {sample.RamAdjustedPercent:F1}%";
        });
    }

    private void OnAlertRaised(object? sender, AlertEvent alertEvent)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var kind = alertEvent.EventType == AlertEventType.Start ? "ALERTA" : "RECUPERADO";
            StatusText = $"{kind}: {alertEvent.Metric} = {alertEvent.RawValue:F1}";
        });
    }

    partial void OnStartWithWindowsChanged(bool value) => AutoStartManager.SetEnabled(value);

    private void OnFaulted(object? sender, Exception ex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = $"Erro: {ex.Message}";
            SetRunning(false);
        });
    }
}
