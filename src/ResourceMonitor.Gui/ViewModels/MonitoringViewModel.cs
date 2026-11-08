using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Gui.Notifications;
using ResourceMonitor.Gui.Services;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;
using Application = System.Windows.Application;

namespace ResourceMonitor.Gui.ViewModels;

public partial class MonitoringViewModel : ObservableObject
{
    private readonly MonitoringController _monitoring;
    private readonly MonitoringService _monitoringService;
    private readonly SettingsSession _settings;
    private readonly ITrayNotifier _trayNotifier;
    private readonly IDialogService _dialogs;
    private readonly IAutoStart _autoStart;
    private string? _lastFaultMessage;

    [ObservableProperty] private int sampleIntervalSeconds;
    [ObservableProperty] private int consecutiveBreachesToAlert;
    [ObservableProperty] private int consecutiveRecoveriesToClear;
    [ObservableProperty] private int topProcessCount;
    [ObservableProperty] private int preEventSeconds;
    [ObservableProperty] private int postEventSeconds;
    [ObservableProperty] private int peakRetentionDays;
    [ObservableProperty] private double cpuPercent;
    [ObservableProperty] private double ramPercent;
    [ObservableProperty] private double diskIoPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatePillText), nameof(EditHintText))]
    private bool isRunning;

    // Mensagens pontuais (alerta, erro, limpeza). O estado ligado/parado fica no selo ao lado.
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string savedNoticeText = string.Empty;
    [ObservableProperty] private string lastSampleText = "Sem amostras ainda.";
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private string newExcludedProcessPattern = string.Empty;

    // Realocado da antiga aba Dados (não é consulta, é ação administrativa — ver Templates).
    [ObservableProperty] private bool clearTrendSelected = true;
    [ObservableProperty] private bool clearPeaksSelected = true;

    public ObservableCollection<string> ExcludedProcesses { get; } = new();
    public ObservableCollection<DiskThresholdRow> DiskThresholds { get; } = new();

    public bool CanEditSettings => !IsRunning;

    public string StatePillText => IsRunning ? "● Monitorando" : "○ Parado";

    public string EditHintText => IsRunning
        ? "Pare o monitoramento para editar as configurações."
        : "As mudanças são gravadas sozinhas ao iniciar, trocar de tela, minimizar ou fechar.";

    public MonitoringViewModel(
        MonitoringController monitoring, SettingsSession settings, ITrayNotifier trayNotifier,
        IDialogService dialogs, IAutoStart autoStart)
    {
        _monitoring = monitoring;
        _monitoringService = monitoring.Service;
        _settings = settings;
        _trayNotifier = trayNotifier;
        _dialogs = dialogs;
        _autoStart = autoStart;

        LoadFrom(settings.Current);
        IsRunning = _monitoringService.IsRunning;
        // Já ligado: a troca de false pra true regrava a entrada no registro
        // (OnStartWithWindowsChanged), o que mantém o caminho do exe em dia se o app mudou de pasta.
        StartWithWindows = _autoStart.IsEnabled();

        _monitoringService.SampleCollected += OnSampleCollected;
        _monitoringService.AlertRaised += OnAlertRaised;
        _monitoringService.DiskSpaceLow += OnDiskSpaceLow;
        _monitoringService.TickFailed += OnTickFailed;
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
        PeakRetentionDays = settings.PeakRetentionDays;
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
        // Não tem campo na tela — preserva o valor que veio do arquivo.
        LogDirectory = _settings.Current.LogDirectory,
        SampleIntervalSeconds = SampleIntervalSeconds,
        ConsecutiveBreachesToAlert = ConsecutiveBreachesToAlert,
        ConsecutiveRecoveriesToClear = ConsecutiveRecoveriesToClear,
        TopProcessCount = TopProcessCount,
        PreEventSeconds = PreEventSeconds,
        PostEventSeconds = PostEventSeconds,
        PeakRetentionDays = PeakRetentionDays,
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

    // Sem botão Salvar: a janela chama isso ao iniciar, trocar de tela, minimizar, fechar e
    // sair (ver MainWindow/App). Só grava se algo mudou (ver SettingsSession.SaveIfChanged); os
    // valores ajustados (ex: intervalo 0 vira 1) voltam pros campos.
    public void SaveIfChanged()
    {
        if (!_settings.SaveIfChanged(BuildSettings()))
        {
            return;
        }

        LoadFrom(_settings.Current);
        SavedNoticeText = $"Configurações salvas às {DateTime.Now:HH:mm:ss}";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void RestoreDefaults()
    {
        LoadFrom(new MonitorSettings());
        StatusText = "Valores padrão restaurados.";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        SaveIfChanged();
        _monitoring.StartByUser();
        // Estado/StatusText são atualizados via RunningStateChanged (ver OnRunningStateChanged) —
        // mesmo caminho usado quando o start vem do menu da bandeja, não só desse botão.
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        await _monitoring.StopByUserAsync();
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
    // Quando o loop para sozinho por falha, OnFaulted chega antes: guarda a mensagem pra ela
    // não ser trocada por um "Parado." genérico.
    private void OnRunningStateChanged(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var isRunning = _monitoringService.IsRunning;
            SetRunning(isRunning);

            if (isRunning)
            {
                _lastFaultMessage = null;
                StatusText = string.Empty;
            }
            else
            {
                StatusText = _lastFaultMessage is { } message ? $"Parado por erro: {message}" : string.Empty;
            }
        });
    }

    partial void OnStartWithWindowsChanged(bool value) => _autoStart.SetEnabled(value);

    [RelayCommand]
    private void ClearSelected()
    {
        if (!ClearTrendSelected && !ClearPeaksSelected)
        {
            return;
        }

        // Exige o monitoramento parado: ClearData abre sua própria conexão, sem coordenar com
        // uma instância de PermanentDatabase que porventura já esteja escrevendo.
        if (_monitoringService.IsRunning)
        {
            _dialogs.ShowWarning(
                "ResourceMonitor",
                "Pare o monitoramento antes de limpar a tendência diária ou a base de picos.");
            return;
        }

        var items = new List<string>();
        if (ClearTrendSelected) items.Add("tendência diária");
        if (ClearPeaksSelected) items.Add("base de picos (eventos, amostras e processos)");

        if (!_dialogs.Confirm("Limpar dados", $"Isso apaga permanentemente: {string.Join(", ", items)}. Continuar?"))
        {
            return;
        }

        PermanentDatabase.ClearData(_settings.DatabasePath, ClearPeaksSelected, ClearTrendSelected);

        StatusText = "Limpeza concluída.";
    }

    private void OnTickFailed(object? sender, Exception ex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusText = $"Aviso: falha temporária na amostragem ({ex.Message}) — tentando de novo.";
        });
    }

    private void OnFaulted(object? sender, Exception ex)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _lastFaultMessage = ex.Message;
            StatusText = $"Erro: {ex.Message}";
            SetRunning(false);
        });
    }
}
