using ResourceMonitor.Sampling;
using Color = System.Windows.Media.Color;

namespace ResourceMonitor.Gui.Controls;

// Cores das métricas num lugar só (as mesmas do antigo chart.html), usadas pelo LineChart.
public static class MetricColors
{
    public static readonly Color Cpu = Color.FromRgb(0x25, 0x63, 0xEB);
    public static readonly Color Ram = Color.FromRgb(0xF9, 0x73, 0x16);
    public static readonly Color Io = Color.FromRgb(0x16, 0xA3, 0x4A);
    public static readonly Color Disk = Color.FromRgb(0x93, 0x33, 0xEA);
}

// Uma linha do gráfico: valores em % (0–100), um por rótulo do eixo X.
public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<double> Values);

// Ponto clicável sobre a linha SeriesIndex, no índice Index do eixo X. Tag volta no clique
// (ver LineChart.MarkerClicked).
public sealed record ChartMarker(int Index, int SeriesIndex, string Label, object? Tag);

// Um ponto da tendência diária: médias do dia em %. Disco em "uso" (100 - livre), pra seguir a
// mesma direção das outras linhas (subiu = mais consumo).
public sealed record DailyChartPoint(string Label, double Cpu, double Ram, double Io, double DiskUsage);

public sealed record ChartData(
    IReadOnlyList<string> XLabels,
    IReadOnlyList<ChartSeries> Series,
    IReadOnlyList<ChartMarker>? Markers = null,
    string? Footnote = null)
{
    // Séries do gráfico diário, na ordem usada pelos marcadores (ver SeriesIndexForMetric).
    public const int CpuSeries = 0;
    public const int RamSeries = 1;
    public const int IoSeries = 2;

    // Home e Gráficos > Dados correntes: últimos ~60 s do monitor ao vivo, CPU/RAM já
    // descontando os processos excluídos.
    public static ChartData FromLiveSamples(IReadOnlyList<ResourceSample> samples) => new(
        samples.Select(s => s.Timestamp.ToLocalTime().ToString("HH:mm:ss")).ToList(),
        new[]
        {
            new ChartSeries("CPU", MetricColors.Cpu, samples.Select(s => s.CpuAdjustedPercent).ToList()),
            new ChartSeries("RAM", MetricColors.Ram, samples.Select(s => s.RamAdjustedPercent).ToList()),
            // "% Disk Time" é um contador agregado (_Total) — o mesmo valor vale pra toda
            // unidade, então basta pegar de qualquer uma presente na amostra.
            new ChartSeries("Disco I/O", MetricColors.Io, samples.Select(s => s.Disks.Count > 0 ? s.Disks[0].IoPercent : 0).ToList()),
        });

    // Tendência e Eventos de picos: uma posição por dia.
    public static ChartData FromDailyPoints(
        IReadOnlyList<DailyChartPoint> points, IReadOnlyList<ChartMarker>? markers = null, string? footnote = null) => new(
        points.Select(p => p.Label).ToList(),
        new[]
        {
            new ChartSeries("CPU", MetricColors.Cpu, points.Select(p => p.Cpu).ToList()),
            new ChartSeries("RAM", MetricColors.Ram, points.Select(p => p.Ram).ToList()),
            new ChartSeries("Disco I/O", MetricColors.Io, points.Select(p => p.Io).ToList()),
            new ChartSeries("Disco (uso)", MetricColors.Disk, points.Select(p => p.DiskUsage).ToList()),
        },
        markers,
        footnote);

    public static int? SeriesIndexForMetric(string metric) => metric switch
    {
        "CPU" => CpuSeries,
        "RAM" => RamSeries,
        "DiscoIO" => IoSeries,
        _ => null,
    };
}
