using System.Text.Json;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Gui.Converters;

// Serialização compartilhada entre ChartViewModel (Gráficos) e HomeViewModel (Home) —
// os dois alimentam o mesmo chart.html/renderSamples, só a origem da amostra muda.
public static class ChartJsonFormatter
{
    public static string ToChartJson(IReadOnlyList<ResourceSample> samples)
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
