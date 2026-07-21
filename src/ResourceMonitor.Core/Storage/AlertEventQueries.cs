using System.Globalization;
using Microsoft.Data.Sqlite;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Storage;

public sealed record AlertEventRow(
    long Id,
    DateTimeOffset Timestamp,
    string EventType,
    string Metric,
    string? DriveName,
    double RawValue,
    double? AdjustedValue,
    double Threshold);

// Consultas de leitura pra GUI — abre uma conexão curta por chamada, pensado pra uso
// interativo (grid, export, gráfico), não pro loop de monitoramento.
public static class AlertEventQueries
{
    public static List<AlertEventRow> GetAlertEvents(string databasePath, DateTimeOffset? from, DateTimeOffset? to)
    {
        if (!File.Exists(databasePath))
        {
            return new List<AlertEventRow>();
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TimestampUtc, EventType, Metric, DriveName, RawValue, AdjustedValue, Threshold
            FROM AlertEvents
            WHERE ($from IS NULL OR TimestampUtc >= $from)
              AND ($to IS NULL OR TimestampUtc <= $to)
            ORDER BY TimestampUtc DESC;
            """;
        command.Parameters.AddWithValue("$from", (object?)FormatTimestamp(from) ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)FormatTimestamp(to) ?? DBNull.Value);

        var results = new List<AlertEventRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AlertEventRow(
                reader.GetInt64(0),
                ParseTimestamp(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.GetDouble(7)));
        }

        return results;
    }

    public static List<ResourceSample> GetSamplesForAlertEvent(string databasePath, long alertEventId)
    {
        if (!File.Exists(databasePath))
        {
            return new List<ResourceSample>();
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        var sampleRows = new List<(long Id, DateTimeOffset Timestamp, double CpuRaw, double CpuAdjusted,
            double RamRaw, double RamAdjusted, double RamTotal, double RamAvailable)>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, TimestampUtc, CpuRawPercent, CpuAdjustedPercent, RamRawPercent, RamAdjustedPercent, RamTotalGb, RamAvailableGb
                FROM Samples
                WHERE AlertEventId = $alertEventId
                ORDER BY TimestampUtc;
                """;
            command.Parameters.AddWithValue("$alertEventId", alertEventId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sampleRows.Add((
                    reader.GetInt64(0),
                    ParseTimestamp(reader.GetString(1)),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7)));
            }
        }

        var result = new List<ResourceSample>(sampleRows.Count);
        foreach (var row in sampleRows)
        {
            var disks = new List<DiskSample>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT DriveName, FreePercent, FreeGb, TotalGb, IoPercent
                    FROM DiskSamples
                    WHERE SampleId = $sampleId;
                    """;
                command.Parameters.AddWithValue("$sampleId", row.Id);

                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    disks.Add(new DiskSample(
                        reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)));
                }
            }

            result.Add(new ResourceSample(
                row.Timestamp, row.CpuRaw, row.CpuAdjusted, row.RamRaw, row.RamAdjusted, row.RamTotal, row.RamAvailable, disks));
        }

        return result;
    }

    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
