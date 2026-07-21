using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public ObservableCollection<ProcessSnapshotRow> TopByCpu { get; } = new();
    public ObservableCollection<ProcessSnapshotRow> TopByRam { get; } = new();

    public event EventHandler<string>? PeakSamplesReady;
    public event EventHandler<string>? LiveSamplesReady;

    public ChartViewModel(MonitoringService monitoringService, Func<string> getDatabasePath, AlertEventQueries alertEventQueries)
    {
        _monitoringService = monitoringService;
        _getDatabasePath = getDatabasePath;
        _alertEventQueries = alertEventQueries;

        _monitoringService.SampleCollected += OnSampleCollected;
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
        });

        return JsonSerializer.Serialize(payload);
    }
}
