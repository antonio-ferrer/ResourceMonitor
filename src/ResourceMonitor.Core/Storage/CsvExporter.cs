using System.Globalization;
using System.Text;

namespace ResourceMonitor.Storage;

public static class CsvExporter
{
    public static void ExportAlertEpisodes(string filePath, IEnumerable<AlertEpisodeRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("StartEventId,Timestamp,Metric,DriveName,DurationMinutes,Interrupted,RawValue,AdjustedValue,Threshold");

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",",
                row.StartEventId.ToString(CultureInfo.InvariantCulture),
                Escape(row.Timestamp.ToLocalTime().ToString("O", CultureInfo.InvariantCulture)),
                Escape(row.Metric),
                Escape(row.DriveName ?? string.Empty),
                row.DurationMinutes?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty,
                row.IsInterrupted ? "true" : "false",
                row.RawValue.ToString("F2", CultureInfo.InvariantCulture),
                row.AdjustedValue?.ToString("F2", CultureInfo.InvariantCulture) ?? string.Empty,
                row.Threshold.ToString("F2", CultureInfo.InvariantCulture)));
        }

        File.WriteAllText(filePath, builder.ToString(), Encoding.UTF8);
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
