using System.Globalization;
using System.Text;

namespace ResourceMonitor.Storage;

public static class CsvExporter
{
    public static void ExportAlertEvents(string filePath, IEnumerable<AlertEventRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Id,Timestamp,EventType,Metric,DriveName,RawValue,AdjustedValue,Threshold");

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",",
                row.Id.ToString(CultureInfo.InvariantCulture),
                Escape(row.Timestamp.ToString("O", CultureInfo.InvariantCulture)),
                Escape(row.EventType),
                Escape(row.Metric),
                Escape(row.DriveName ?? string.Empty),
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
