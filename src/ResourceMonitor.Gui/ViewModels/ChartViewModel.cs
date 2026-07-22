using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Gui.ViewModels;

public partial class ChartViewModel : ObservableObject
{
    private readonly MonitoringService _monitoringService;
    private readonly Func<string> _getDatabasePath;
    private readonly AlertEventQueries _alertEventQueries;

    [ObservableProperty] private long? currentAlertEventId;
    [ObservableProperty] private string statusText = "Selecione um evento na aba Dados.";
    [ObservableProperty] private string liveStatusText = "Aguardando o monitoramento iniciar...";
    [ObservableProperty] private string trendStatusText = "Tendência diária (últimos 30 dias)";

    public ObservableCollection<ProcessSnapshotRow> TopByCpu { get; } = new();
    public ObservableCollection<ProcessSnapshotRow> TopByRam { get; } = new();
    public ObservableCollection<ProcessSnapshotRow> TopByIo { get; } = new();

    public event EventHandler<string>? PeakSamplesReady;
    public event EventHandler<string>? LiveSamplesReady;
    public event EventHandler<string>? DailyTrendReady;

    public ChartViewModel(MonitoringService monitoringService, Func<string> getDatabasePath, AlertEventQueries alertEventQueries)
    {
        _monitoringService = monitoringService;
        _getDatabasePath = getDatabasePath;
        _alertEventQueries = alertEventQueries;

        _monitoringService.SampleCollected += OnSampleCollected;
    }

    [RelayCommand]
    private void LoadDailyTrend()
    {
        var databasePath = _getDatabasePath();
        var to = DateOnly.FromDateTime(DateTime.Today);
        var from = to.AddDays(-30);

        var rows = _alertEventQueries.GetDailyAggregates(databasePath, from, to);

        TrendStatusText = rows.Count == 0
            ? "Tendência diária: sem capturas ainda (aguarde ~5min de monitoramento)."
            : $"Tendência diária: {rows.Count} dia(s) nos últimos 30.";

        // Disco vira "em uso" (100 - livre) pra seguir a mesma direção das outras 3 linhas
        // (subiu = mais consumo), em vez de misturar com "espaço livre" (subiu = bom).
        var payload = rows.Select(r => new
        {
            date = r.Date.ToString("dd/MM"),
            cpu = Math.Round(r.AvgCpuRawPercent, 1),
            ram = Math.Round(r.AvgRamRawPercent, 1),
            io = Math.Round(r.AvgIoPercent, 1),
            diskUsage = Math.Round(100 - r.AvgDiskFreePercent, 1),
        });

        DailyTrendReady?.Invoke(this, JsonSerializer.Serialize(payload));
    }

    private void OnSampleCollected(object? sender, ResourceSample sample)
    {
        var samples = _monitoringService.GetCurrentSamples();
        var json = ToChartJson(samples);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            LiveStatusText = $"{samples.Count} amostra(s) no cache.";
            LiveSamplesReady?.Invoke(this, json);
        });
    }

    public void LoadForAlertEvent(long alertEventId)
    {
        CurrentAlertEventId = alertEventId;
        var databasePath = _getDatabasePath();

        var snapshots = _alertEventQueries.GetProcessSnapshotsForAlertEvent(databasePath, alertEventId);
        TopByCpu.Clear();
        TopByRam.Clear();
        TopByIo.Clear();
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Kind == "Cpu")
            {
                TopByCpu.Add(snapshot);
            }
            else if (snapshot.Kind == "Ram")
            {
                TopByRam.Add(snapshot);
            }
            else if (snapshot.Kind == "Io")
            {
                TopByIo.Add(snapshot);
            }
        }

        var samples = _alertEventQueries.GetSamplesForAlertEvent(databasePath, alertEventId);

        StatusText = samples.Count == 0
            ? $"Evento #{alertEventId}: sem amostras capturadas (janela ainda pendente ou fora do intervalo)."
            : $"Evento #{alertEventId}: {samples.Count} amostra(s).";

        // Sempre dispara, mesmo com lista vazia — senão o WebView2 fica com o desenho do
        // evento selecionado anteriormente (o chart.html já sabe mostrar "sem dados" com []).
        PeakSamplesReady?.Invoke(this, ToChartJson(samples));
    }

    private static string ToChartJson(IReadOnlyList<ResourceSample> samples)
    {
        var payload = samples.Select(s => new
        {
            timestamp = s.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
            cpu = Math.Round(s.CpuAdjustedPercent, 1),
            ram = Math.Round(s.RamAdjustedPercent, 1),
            // "% Disk Time" é um contador agregado (_Total) — o mesmo valor vale pra toda
            // unidade, então basta pegar de qualquer uma presente na amostra.
            io = s.Disks.Count > 0 ? Math.Round(s.Disks[0].IoPercent, 1) : 0,
        });

        return JsonSerializer.Serialize(payload);
    }
}
