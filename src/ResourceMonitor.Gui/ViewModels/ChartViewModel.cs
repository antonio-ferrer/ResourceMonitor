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

    [ObservableProperty] private long? currentAlertEventId;
    [ObservableProperty] private string statusText = "Selecione um evento na aba Dados.";
    [ObservableProperty] private string liveStatusText = "Aguardando o monitoramento iniciar...";

    public event EventHandler<string>? PeakSamplesReady;
    public event EventHandler<string>? LiveSamplesReady;

    public ChartViewModel(MonitoringService monitoringService, Func<string> getDatabasePath)
    {
        _monitoringService = monitoringService;
        _getDatabasePath = getDatabasePath;

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
        var samples = AlertEventQueries.GetSamplesForAlertEvent(databasePath, alertEventId);

        if (samples.Count == 0)
        {
            StatusText = $"Evento #{alertEventId}: sem amostras capturadas (janela ainda pendente ou fora do intervalo).";
            return;
        }

        StatusText = $"Evento #{alertEventId}: {samples.Count} amostra(s).";
        PeakSamplesReady?.Invoke(this, ToChartJson(samples));
    }

    private static string ToChartJson(IReadOnlyList<ResourceSample> samples)
    {
        var payload = samples.Select(s => new
        {
            timestamp = s.Timestamp.ToString("HH:mm:ss"),
            cpu = Math.Round(s.CpuAdjustedPercent, 1),
            ram = Math.Round(s.RamAdjustedPercent, 1),
        });

        return JsonSerializer.Serialize(payload);
    }
}
