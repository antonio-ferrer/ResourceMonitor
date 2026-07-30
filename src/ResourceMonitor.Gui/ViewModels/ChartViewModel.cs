using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Gui.Converters;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Gui.ViewModels;

public partial class ChartViewModel : ObservableObject
{
    private readonly Func<string> _getDatabasePath;
    private readonly AlertEventQueries _alertEventQueries;

    [ObservableProperty] private long? currentAlertEventId;
    [ObservableProperty] private string statusText = "Selecione um evento na aba Dados.";
    [ObservableProperty] private string liveStatusText = "Aguardando amostras...";
    [ObservableProperty] private string trendStatusText = "Tendência diária (últimos 30 dias)";

    public ObservableCollection<ProcessSnapshotRow> TopByCpu { get; } = new();
    public ObservableCollection<ProcessSnapshotRow> TopByRam { get; } = new();
    public ObservableCollection<ProcessSnapshotRow> TopByIo { get; } = new();

    public event EventHandler<string>? PeakSamplesReady;
    public event EventHandler<string>? LiveSamplesReady;
    public event EventHandler<string>? DailyTrendReady;

    public ChartViewModel(LiveMonitorService liveMonitor, Func<string> getDatabasePath, AlertEventQueries alertEventQueries)
    {
        _getDatabasePath = getDatabasePath;
        _alertEventQueries = alertEventQueries;

        // Fonte independente de Iniciar/Parar — ver LiveMonitorService. Compartilhada com a
        // Home (mesmo mecanismo, sem amostragem duplicada).
        liveMonitor.SnapshotUpdated += OnLiveSnapshotUpdated;
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

    private void OnLiveSnapshotUpdated(object? sender, LiveSnapshot snapshot)
    {
        var json = ChartJsonFormatter.ToChartJson(snapshot.Samples);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            LiveStatusText = $"{snapshot.Samples.Count} amostra(s) (últimos ~60s).";
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
        PeakSamplesReady?.Invoke(this, ChartJsonFormatter.ToChartJson(samples));
    }
}
