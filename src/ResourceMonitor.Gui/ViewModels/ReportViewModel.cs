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

        var dailyTrend = _alertEventQueries.GetDailyAggregates(
            databasePath, DateOnly.FromDateTime(effectiveFrom), DateOnly.FromDateTime(effectiveTo));

        var hardware = HardwareInfoReader.Capture();
        var diskProjection = BuildDiskProjection(dailyTrend, hardware);
        var hourlyPattern = BuildHourlyPattern(events);
        var payload = BuildPayload(events, hardware, effectiveFrom, effectiveTo, dailyTrend, diskProjection, hourlyPattern);
        var json = JsonSerializer.Serialize(payload);

        StatusText = $"Relatório gerado: {events.Count} evento(s) no período.";
        ReportReady?.Invoke(this, json);
    }

    private object BuildPayload(
        List<AlertEpisodeRow> events, HardwareInfo hardware, DateTime effectiveFrom, DateTime effectiveTo,
        List<DailyAggregateRow> dailyTrend, object diskProjection, object hourlyPattern)
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
            hourlyPattern,
            dailyTrendSystemDrive = dailyTrend.Count > 0 ? dailyTrend[0].SystemDrive : "—",
            // Valores numéricos (não texto formatado) — o gráfico de canvas precisa plotar
            // as coordenadas, diferente do resto do relatório que só exibe rótulos prontos.
            // Disco vira "em uso" (100 - livre) pra seguir a mesma direção das outras 3 linhas
            // (subiu = mais consumo), em vez de misturar com "espaço livre" (subiu = bom).
            dailyTrend = dailyTrend.Select(d => new
            {
                dateLabel = d.Date.ToString("dd/MM", PtBr),
                avgCpu = Math.Round(d.AvgCpuRawPercent, 1),
                avgRam = Math.Round(d.AvgRamRawPercent, 1),
                avgIo = Math.Round(d.AvgIoPercent, 1),
                avgDiskUsage = Math.Round(100 - d.AvgDiskFreePercent, 1),
            }),
            diskProjection,
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
            }),
        };
    }

    // Regressão linear simples (mínimos quadrados) de AvgDiskFreePercent contra o dia dentro
    // do período — só a unidade do sistema, já que DailyAggregates não guarda histórico por
    // disco (ver MonitoringService). "available=false" cobre tanto dado insuficiente quanto
    // tendência estável/de recuperação, pra não mostrar um número de projeção sem sentido.
    private static object BuildDiskProjection(List<DailyAggregateRow> dailyTrend, HardwareInfo hardware)
    {
        const int minPoints = 3;
        const double flatSlopeTolerance = -0.01; // %/dia — abaixo disso é ruído, não tendência
        const int maxProjectedDays = 3650; // ~10 anos — além disso não tem valor prático

        if (dailyTrend.Count < minPoints)
        {
            return new { available = false, message = $"Dados insuficientes no período selecionado (mínimo de {minPoints} dias com amostras) para estimar tendência." };
        }

        var firstDate = dailyTrend[0].Date;
        var xs = dailyTrend.Select(d => (double)d.Date.DayNumber - firstDate.DayNumber).ToArray();
        var ys = dailyTrend.Select(d => d.AvgDiskFreePercent).ToArray();

        var xMean = xs.Average();
        var yMean = ys.Average();
        var covariance = xs.Zip(ys, (x, y) => (x - xMean) * (y - yMean)).Sum();
        var variance = xs.Sum(x => (x - xMean) * (x - xMean));

        if (variance == 0)
        {
            return new { available = false, message = "Dados insuficientes no período selecionado (todas as amostras no mesmo dia) para estimar tendência." };
        }

        var slope = covariance / variance;

        if (slope >= flatSlopeTolerance)
        {
            return new { available = false, message = "Espaço livre estável ou aumentando no período selecionado — sem projeção de esgotamento no ritmo atual." };
        }

        var intercept = yMean - slope * xMean;
        var lastX = xs[^1];
        var freeAtLastDay = intercept + slope * lastX;
        var daysToZero = -freeAtLastDay / slope;

        var driveLabel = dailyTrend[^1].SystemDrive;

        if (daysToZero > maxProjectedDays)
        {
            return new { available = false, message = $"Queda de espaço livre muito lenta no período selecionado (mais de {maxProjectedDays / 365} anos no ritmo atual) — sem projeção prática." };
        }

        var wholeDays = Math.Max(0, (int)Math.Round(daysToZero));
        var estimatedDate = dailyTrend[^1].Date.ToDateTime(TimeOnly.MinValue).AddDays(wholeDays);

        var driveTotalGb = hardware.Disks.FirstOrDefault(d => string.Equals(d.DriveName, driveLabel, StringComparison.OrdinalIgnoreCase))?.TotalGb;
        var dropRateGbPerDayLabel = driveTotalGb is { } totalGb
            ? $" (~{(-slope / 100 * totalGb).ToString("N1", PtBr)} GB/dia)"
            : string.Empty;

        return new
        {
            available = true,
            driveLabel,
            dropRateLabel = $"{(-slope).ToString("N2", PtBr)} %/dia{dropRateGbPerDayLabel}",
            daysLabel = $"{wholeDays} dia{(wholeDays == 1 ? "" : "s")}",
            estimatedDateLabel = estimatedDate.ToString("dd/MM/yyyy", PtBr),
        };
    }

    // Segunda a domingo em vez da ordem padrão do DayOfWeek (que começa no domingo) — mais
    // fácil de reconhecer um padrão de semana de trabalho ("toda sexta às 14h").
    private static readonly int[] WeekDisplayOrder = { 1, 2, 3, 4, 5, 6, 0 };
    private static readonly string[] WeekDayAbbreviations = { "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb", "Dom" };

    // Agrupa os episódios (já filtrados por métrica/período, mesma lista da tabela "Todos
    // os eventos") por dia da semana × hora do dia, em hora local — só faz sentido pra
    // identificar padrão de recorrência ("toda sexta às 14h") na hora que a pessoa vive.
    private static object BuildHourlyPattern(List<AlertEpisodeRow> events)
    {
        if (events.Count == 0)
        {
            return new { available = false };
        }

        var grid = new int[7, 24];
        foreach (var e in events)
        {
            var local = e.Timestamp.ToLocalTime();
            grid[(int)local.DayOfWeek, local.Hour]++;
        }

        var maxCount = 0;
        var topDayIndex = 0;
        var topHour = 0;
        for (var day = 0; day < 7; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                if (grid[day, hour] > maxCount)
                {
                    maxCount = grid[day, hour];
                    topDayIndex = day;
                    topHour = hour;
                }
            }
        }

        var rows = WeekDisplayOrder.Select((dayIndex, i) => new
        {
            dayLabel = WeekDayAbbreviations[i],
            counts = Enumerable.Range(0, 24).Select(hour => grid[dayIndex, hour]),
        });

        var topSlotLabel = maxCount > 0
            ? $"{PtBr.DateTimeFormat.GetDayName((DayOfWeek)topDayIndex)} às {topHour:00}h ({maxCount} ocorrência{(maxCount == 1 ? "" : "s")})"
            : null;

        return new
        {
            available = true,
            rows,
            maxCount,
            topSlotLabel,
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
