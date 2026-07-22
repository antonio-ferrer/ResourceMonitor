using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Gui.ViewModels;

// Monta o payload (JSON) consumido por Assets/report.html via renderReport(data) — toda
// formatação pt-BR e classificação de status (completo/interrompido/em andamento) já sai
// pronta daqui, o JS só faz a montagem do DOM, sem duplicar regra de negócio.
public partial class ReportViewModel : ObservableObject
{
    private static readonly CultureInfo PtBr = new("pt-BR");

    private readonly Func<string> _getDatabasePath;
    private readonly AlertEventQueries _alertEventQueries;

    [ObservableProperty] private DateTime? periodFrom = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime? periodTo = DateTime.Today;
    [ObservableProperty] private bool includeCpu = true;
    [ObservableProperty] private bool includeRam = true;
    [ObservableProperty] private bool includeDiscoIo = true;
    [ObservableProperty] private bool includeAllEvents = true;
    [ObservableProperty] private string statusText = string.Empty;

    public event EventHandler<string>? ReportReady;

    public ReportViewModel(Func<string> getDatabasePath, AlertEventQueries alertEventQueries)
    {
        _getDatabasePath = getDatabasePath;
        _alertEventQueries = alertEventQueries;
    }

    [RelayCommand]
    private void GerarRelatorio()
    {
        var effectiveFrom = (PeriodFrom ?? DateTime.Today.AddDays(-7)).Date;
        var effectiveTo = (PeriodTo ?? DateTime.Today).Date;

        var databasePath = _getDatabasePath();
        var from = new DateTimeOffset(effectiveFrom);
        var to = new DateTimeOffset(effectiveTo.AddDays(1).AddTicks(-1));

        var selectedMetrics = new HashSet<string>();
        if (IncludeCpu) selectedMetrics.Add("CPU");
        if (IncludeRam) selectedMetrics.Add("RAM");
        if (IncludeDiscoIo) selectedMetrics.Add("DiscoIO");

        var events = _alertEventQueries.GetAlertEpisodes(databasePath, from, to)
            .Where(e => selectedMetrics.Contains(e.Metric))
            .OrderBy(e => e.Timestamp)
            .ToList();

        var hardware = HardwareInfoReader.Capture();
        var payload = BuildPayload(events, hardware, effectiveFrom, effectiveTo);
        var json = JsonSerializer.Serialize(payload);

        StatusText = $"Relatório gerado: {events.Count} evento(s) no período.";
        ReportReady?.Invoke(this, json);
    }

    private object BuildPayload(List<AlertEpisodeRow> events, HardwareInfo hardware, DateTime effectiveFrom, DateTime effectiveTo)
    {
        var withDuration = events.Where(e => e.DurationMinutes.HasValue).ToList();
        var ongoingCount = events.Count - withDuration.Count;
        var interruptedCount = events.Count(e => e.IsInterrupted);
        var totalMinutes = withDuration.Sum(e => e.DurationMinutes!.Value);
        var biggest = withDuration.OrderByDescending(e => e.DurationMinutes).FirstOrDefault();

        var totalEventsSub = ongoingCount > 0
            ? $"{events.Count - ongoingCount} completos · {ongoingCount} em andamento"
            : $"{events.Count} completos";

        return new
        {
            machineName = hardware.MachineName,
            periodFrom = effectiveFrom.ToString("dd/MM/yyyy", PtBr),
            periodTo = effectiveTo.ToString("dd/MM/yyyy", PtBr),
            generatedAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm", PtBr),
            includeEvents = IncludeAllEvents,
            hardware = new
            {
                operatingSystem = hardware.OperatingSystem,
                processorName = hardware.ProcessorName,
                ramTotalLabel = $"{hardware.RamTotalGb.ToString("N1", PtBr)} GB",
                disks = hardware.Disks.Select(d => new
                {
                    label = $"Disco {d.DriveName}",
                    detail = $"Total {d.TotalGb.ToString("N1", PtBr)} GB · Livre {d.FreeGb.ToString("N1", PtBr)} GB ({d.FreePercent.ToString("N1", PtBr)}%)",
                }),
            },
            summary = new
            {
                totalEvents = events.Count,
                totalEventsSub,
                interruptedEvents = interruptedCount,
                totalDurationLabel = FormatTotalDuration(totalMinutes),
                biggestPeakLabel = biggest is null ? "—" : FormatDuration(biggest),
                biggestPeakSub = biggest is null
                    ? ""
                    : $"{FormatMetricLabel(biggest.Metric)} · {biggest.Timestamp.ToLocalTime().ToString("dd/MM", PtBr)}",
                hasInterrupted = withDuration.Any(e => e.IsInterrupted),
                hasOngoing = ongoingCount > 0,
            },
            byMetric = events
                .GroupBy(e => e.Metric)
                .OrderBy(g => MetricSortOrder(g.Key))
                .Select(g =>
                {
                    var groupWithDuration = g.Where(e => e.DurationMinutes.HasValue).ToList();
                    var sum = groupWithDuration.Sum(e => e.DurationMinutes!.Value);
                    var hasInterruptedInGroup = groupWithDuration.Any(e => e.IsInterrupted);
                    var eventsLabel = groupWithDuration.Count == g.Count()
                        ? g.Count().ToString(PtBr)
                        : $"{g.Count()} ({groupWithDuration.Count} completos)";

                    return new
                    {
                        metric = FormatMetricLabel(g.Key),
                        dotClass = DotClass(g.Key),
                        eventsLabel,
                        totalLabel = groupWithDuration.Count == 0
                            ? "—"
                            : $"{sum.ToString("N1", PtBr)} min{(hasInterruptedInGroup ? "*" : "")}",
                        avgLabel = groupWithDuration.Count == 0
                            ? "—"
                            : $"{(sum / groupWithDuration.Count).ToString("N1", PtBr)} min",
                    };
                }),
            events = events.Select(e => new
            {
                timestamp = e.Timestamp.ToLocalTime().ToString("dd/MM HH:mm:ss", PtBr),
                metric = FormatMetricLabel(e.Metric),
                dotClass = DotClass(e.Metric),
                driveName = e.DriveName ?? "—",
                durationLabel = FormatDuration(e),
                rawLabel = $"{e.RawValue.ToString("N1", PtBr)}%",
                adjustedLabel = e.AdjustedValue is { } adjusted ? $"{adjusted.ToString("N1", PtBr)}%" : "—",
                thresholdLabel = $"{e.Threshold.ToString("N1", PtBr)}%",
                statusClass = e.DurationMinutes is null ? "ongoing" : e.IsInterrupted ? "interrupted" : "ok",
                statusLabel = e.DurationMinutes is null ? "Em andamento" : e.IsInterrupted ? "Interrompido" : "Completo",
            }),
        };
    }

    private static string FormatDuration(AlertEpisodeRow episode)
    {
        if (episode.DurationMinutes is not { } minutes)
        {
            return "—";
        }

        var formatted = $"{minutes.ToString("N1", PtBr)} min";
        return episode.IsInterrupted ? $"maior que {formatted}*" : formatted;
    }

    private static string FormatTotalDuration(double totalMinutes)
    {
        var totalWholeMinutes = (int)Math.Round(totalMinutes);
        var hours = totalWholeMinutes / 60;
        var minutes = totalWholeMinutes % 60;
        return hours > 0 ? $"{hours}h {minutes}min" : $"{minutes}min";
    }

    private static string FormatMetricLabel(string metric) => metric switch
    {
        "CPU" => "CPU",
        "RAM" => "RAM",
        "DiscoIO" => "Disco (I/O)",
        _ => metric,
    };

    private static string DotClass(string metric) => metric switch
    {
        "CPU" => "cpu",
        "RAM" => "ram",
        "DiscoIO" => "io",
        _ => "",
    };

    private static int MetricSortOrder(string metric) => metric switch
    {
        "CPU" => 0,
        "RAM" => 1,
        "DiscoIO" => 2,
        _ => 3,
    };
}
