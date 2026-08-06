using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Gui;
using ResourceMonitor.Gui.Notifications;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace ResourceMonitor.Gui.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private readonly MonitoringService _monitoringService;
    private readonly string _dataDirectory;
    private readonly ITrayNotifier _trayNotifier;
    private readonly Func<string> _getDatabasePath;

    [ObservableProperty] private int sampleIntervalSeconds;
    [ObservableProperty] private int consecutiveBreachesToAlert;
    [ObservableProperty] private int consecutiveRecoveriesToClear;
    [ObservableProperty] private int topProcessCount;
    [ObservableProperty] private int preEventSeconds;
    [ObservableProperty] private int postEventSeconds;
    [ObservableProperty] private double cpuPercent;
    [ObservableProperty] private double ramPercent;
    [ObservableProperty] private double diskIoPercent;

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string statusText = "Parado.";
    [ObservableProperty] private string lastSampleText = "Sem amostras ainda.";
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private string newExcludedProcessPattern = string.Empty;

    // Realocado da antiga aba Dados (não é consulta, é ação administrativa — ver Templates).
    [ObservableProperty] private bool clearCacheSelected = true;
    [ObservableProperty] private bool clearTrendSelected = true;
    [ObservableProperty] private bool clearPeaksSelected = true;

    public ObservableCollection<string> ExcludedProcesses { get; } = new();
    public ObservableCollection<DiskThresholdRow> DiskThresholds { get; } = new();

    public bool CanEditSettings => !IsRunning;

    public MonitoringViewModel(
        MonitoringService monitoringService, MonitorSettings initialSettings, string dataDirectory,
        ITrayNotifier trayNotifier, Func<string> getDatabasePath)
    {
        _monitoringService = monitoringService;
        _dataDirectory = dataDirectory;
        _trayNotifier = trayNotifier;
        _getDatabasePath = getDatabasePath;

        LoadFrom(initialSettings);
        IsRunning = _monitoringService.IsRunning;
        StartWithWindows = AutoStartManager.IsEnabled();

        // Se o monitoramento já foi iniciado antes da janela existir (boot via --minimized,
        // ver App.xaml.cs), o texto de status precisa refletir isso — senão fica preso em
        // "Parado." mesmo com os botões já mostrando IsRunning = true.
        if (IsRunning)
        {
            StatusText = "Monitorando...";
        }

        _monitoringService.SampleCollected += OnSampleCollected;
        _monitoringService.AlertRaised += OnAlertRaised;
        _monitoringService.DiskSpaceLow += OnDiskSpaceLow;
        _monitoringService.Faulted += OnFaulted;
        _monitoringService.RunningStateChanged += OnRunningStateChanged;
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
        DiskIoPercent = settings.Thresholds.DiskIoPercent;

        ExcludedProcesses.Clear();
        foreach (var pattern in settings.ExcludedProcesses)
        {
            ExcludedProcesses.Add(pattern);
        }

        // Lista de discos é auto-populada a partir dos discos fixos reais da máquina (não do
        // JSON) — só o valor de MinFreePercent vem do settings, quando já existia; disco novo
        // (nunca configurado) recebe o default. Disco salvo que sumiu da máquina não aparece.
        DiskThresholds.Clear();
        var savedThresholds = settings.Thresholds.DiskFreeThresholds
            .ToDictionary(t => t.DriveName, t => t.MinFreePercent, StringComparer.OrdinalIgnoreCase);
        foreach (var driveName in DiskMonitor.GetFixedDriveNames())
        {
            var minFreePercent = savedThresholds.TryGetValue(driveName, out var savedValue)
                ? savedValue
                : DiskThreshold.DefaultMinFreePercent;
            DiskThresholds.Add(new DiskThresholdRow(driveName, minFreePercent));
        }
    }

    private MonitorSettings BuildSettings() => new()
    {
        SampleIntervalSeconds = SampleIntervalSeconds,
        ConsecutiveBreachesToAlert = ConsecutiveBreachesToAlert,
        ConsecutiveRecoveriesToClear = ConsecutiveRecoveriesToClear,
        TopProcessCount = TopProcessCount,
        PreEventSeconds = PreEventSeconds,
        PostEventSeconds = PostEventSeconds,
        ExcludedProcesses = ExcludedProcesses.ToList(),
        Thresholds = new ThresholdSettings
        {
            CpuPercent = CpuPercent,
            RamPercent = RamPercent,
            DiskIoPercent = DiskIoPercent,
            DiskFreeThresholds = DiskThresholds
                .Select(r => new DiskThreshold { DriveName = r.DriveName, MinFreePercent = r.MinFreePercent })
                .ToList(),
        },
    };

    [RelayCommand]
    private void AddExcludedProcess()
    {
        var pattern = NewExcludedProcessPattern.Trim();
        if (pattern.Length == 0)
        {
            return;
        }

        if (!ExcludedProcesses.Any(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            ExcludedProcesses.Add(pattern);
        }

        NewExcludedProcessPattern = string.Empty;
    }

    [RelayCommand]
    private void RemoveExcludedProcess(string pattern)
    {
        ExcludedProcesses.Remove(pattern);
    }

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
        // Estado/StatusText são atualizados via RunningStateChanged (ver OnRunningStateChanged) —
        // mesmo caminho usado quando o start vem do menu da bandeja, não só desse botão.
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        await _monitoringService.StopAsync();
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
                $"[{sample.Timestamp.ToLocalTime():HH:mm:ss}] CPU {sample.CpuAdjustedPercent:F1}% | RAM {sample.RamAdjustedPercent:F1}%";
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

    private void OnDiskSpaceLow(object? sender, DiskSpaceWarning warning)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = $"AVISO: espaço em disco baixo em {warning.DriveName} ({warning.FreePercent:F1}%)";
            _trayNotifier.ShowWarning(
                "Alerta de espaço em disco",
                $"{warning.DriveName}: {warning.FreePercent:F1}% livre (mínimo {warning.MinFreePercent:F1}%)");
        });
    }

    // Fonte única de verdade pro estado Iniciar/Parar da janela — dispara independente de
    // quem iniciou/parou o monitoramento (esse botão, o menu da bandeja, ou --minimized).
    private void OnRunningStateChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            SetRunning(_monitoringService.IsRunning);
            StatusText = _monitoringService.IsRunning ? "Monitorando..." : "Parado.";
        });
    }

    partial void OnStartWithWindowsChanged(bool value) => AutoStartManager.SetEnabled(value);

    [RelayCommand]
    private void ClearSelected()
    {
        if (!ClearCacheSelected && !ClearTrendSelected && !ClearPeaksSelected)
        {
            return;
        }

        // Cache é em memória, sem tabela em disco — só picos/tendência exigem o
        // monitoramento parado (ClearData abre sua própria conexão, sem coordenar com
        // uma instância de PermanentDatabase que porventura já esteja escrevendo).
        if ((ClearTrendSelected || ClearPeaksSelected) && _monitoringService.IsRunning)
        {
            MessageBox.Show(
                "Pare o monitoramento antes de limpar a tendência diária ou a base de picos.",
                "ResourceMonitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var items = new List<string>();
        if (ClearCacheSelected) items.Add("cache (amostras em memória)");
        if (ClearTrendSelected) items.Add("tendência diária");
        if (ClearPeaksSelected) items.Add("base de picos (eventos, amostras e processos)");

        var confirm = MessageBox.Show(
            $"Isso apaga permanentemente: {string.Join(", ", items)}. Continuar?",
            "Limpar dados",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        if (ClearCacheSelected)
        {
            _monitoringService.ClearCache();
        }

        if (ClearTrendSelected || ClearPeaksSelected)
        {
            PermanentDatabase.ClearData(_getDatabasePath(), ClearPeaksSelected, ClearTrendSelected);
        }

        StatusText = "Limpeza concluída.";
    }

    private void OnFaulted(object? sender, Exception ex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = $"Erro: {ex.Message}";
            SetRunning(false);
        });
    }
}
