using System.Globalization;
using Microsoft.Data.Sqlite;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Storage;

// Um "episódio" é o par Start+End de um alerta, composto na hora da consulta — a tabela
// continua guardando os dois eventos separados (ver PermanentDatabase).
// DurationMinutes fica null só quando o alerta está genuinamente em andamento (monitoramento
// ainda rodando, sem End e sem ter sido marcado como interrompido). Quando IsInterrupted é
// true, DurationMinutes é uma duração MÍNIMA conhecida (via heartbeat), não a duração real.
public sealed record AlertEpisodeRow(
    long StartEventId,
    DateTimeOffset Timestamp,
    string Metric,
    string? DriveName,
    double? DurationMinutes,
    bool IsInterrupted,
    double RawValue,
    double? AdjustedValue,
    double Threshold);

public sealed record ProcessSnapshotRow(
    string Kind,
    string ProcessName,
    int ProcessId,
    double CpuPercent,
    double RamMb,
    double IoKbPerSec);

// Médias já calculadas (Sum/SampleCount) — pensado pra tendência de uso ao longo de dias,
// não pra alerta. AvgDiskFreePercent é só do disco do sistema (ver MonitoringService).
public sealed record DailyAggregateRow(
    DateOnly Date,
    int SampleCount,
    double AvgCpuRawPercent,
    double AvgRamRawPercent,
    double AvgIoPercent,
    double AvgDiskFreePercent,
    string SystemDrive);

// Consultas de leitura pra GUI — abre uma conexão curta por chamada, pensado pra uso
// interativo (grid, export, gráfico), não pro loop de monitoramento.
public sealed class AlertEventQueries
{
    private readonly ITraceLogger _traceLogger;

    public AlertEventQueries(ITraceLogger traceLogger)
    {
        _traceLogger = traceLogger;
    }

    public List<AlertEpisodeRow> GetAlertEpisodes(string databasePath, DateTimeOffset? from, DateTimeOffset? to)
    {
        _traceLogger.Trace("AlertEventQueries",
            $"GetAlertEpisodes chamado. databasePath='{databasePath}' from='{from:O}' to='{to:O}'");

        if (!File.Exists(databasePath))
        {
            _traceLogger.Trace("AlertEventQueries", "Banco não existe nesse caminho. Retornando lista vazia.");
            return new List<AlertEpisodeRow>();
        }

        // Garante que um banco de uma versão anterior do app (sem LastActiveUtc/Interrupted)
        // já esteja migrado antes de consultar essas colunas — a conexão abaixo é somente
        // leitura e não pode rodar ALTER TABLE.
        PermanentDatabase.EnsureSchema(databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        // Busca tudo sem filtro de data aqui — o pareamento Start/End precisa do histórico
        // completo (um End pode existir fora da janela mesmo com o Start dentro, ou vice-versa).
        // O filtro from/to é aplicado depois, em cima do timestamp do Start de cada episódio.
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TimestampUtc, EventType, Metric, DriveName, RawValue, AdjustedValue, Threshold, LastActiveUtc, Interrupted
            FROM AlertEvents
            ORDER BY TimestampUtc;
            """;

        var rawRows = new List<(long Id, DateTimeOffset Timestamp, string EventType, string Metric,
            string? DriveName, double RawValue, double? AdjustedValue, double Threshold,
            DateTimeOffset? LastActiveUtc, bool Interrupted)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rawRows.Add((
                    reader.GetInt64(0),
                    ParseTimestamp(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetDouble(5),
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : ParseTimestamp(reader.GetString(8)),
                    reader.GetInt64(9) != 0));
            }
        }

        // Pareia cada Start com o End seguinte de mesma métrica+unidade.
        var openStarts = new Dictionary<string, (long Id, DateTimeOffset Timestamp, string Metric,
            string? DriveName, double RawValue, double? AdjustedValue, double Threshold,
            DateTimeOffset? LastActiveUtc, bool Interrupted)>();
        var episodes = new List<AlertEpisodeRow>();

        foreach (var row in rawRows)
        {
            var key = $"{row.Metric}|{row.DriveName}";

            if (row.EventType == "Start")
            {
                // Já havia um Start aberto pra essa chave (nunca recebeu End) — normalmente porque
                // uma execução anterior foi interrompida antes do alerta recuperar. Emite ele como
                // episódio próprio ANTES de abrir o novo: senão a sobrescrita abaixo o descartaria
                // silenciosamente (o pico antigo "sumia" quando o monitoramento reiniciava e
                // disparava um novo Start pra mesma métrica).
                if (openStarts.TryGetValue(key, out var previousOpen))
                {
                    episodes.Add(BuildOpenEpisode(previousOpen));
                }

                openStarts[key] = (row.Id, row.Timestamp, row.Metric, row.DriveName, row.RawValue,
                    row.AdjustedValue, row.Threshold, row.LastActiveUtc, row.Interrupted);
            }
            else if (row.EventType == "End" && openStarts.TryGetValue(key, out var start))
            {
                var durationMinutes = (row.Timestamp - start.Timestamp).TotalMinutes;
                episodes.Add(new AlertEpisodeRow(
                    start.Id, start.Timestamp, start.Metric, start.DriveName, durationMinutes, false,
                    start.RawValue, start.AdjustedValue, start.Threshold));
                openStarts.Remove(key);
            }
        }

        // Starts sem End (fim da lista inteira): mesmo caso do desvio acima, só que pro último
        // Start aberto de cada chave, que não teve um Start seguinte pra forçar a emissão dele.
        foreach (var start in openStarts.Values)
        {
            episodes.Add(BuildOpenEpisode(start));
        }

        var results = episodes
            .Where(ep => (from is null || ep.Timestamp >= from) && (to is null || ep.Timestamp <= to))
            .OrderByDescending(ep => ep.Timestamp)
            .ToList();

        _traceLogger.Trace("AlertEventQueries",
            $"GetAlertEpisodes retornou {results.Count} episódio(s) de {rawRows.Count} evento(s) crus.");

        return results;
    }

    // Um Start sem End correspondente: se foi marcado como interrompido (Parar manual, crash),
    // mostra a duração mínima conhecida via heartbeat; senão é genuinamente "em andamento" agora.
    private static AlertEpisodeRow BuildOpenEpisode(
        (long Id, DateTimeOffset Timestamp, string Metric, string? DriveName, double RawValue,
            double? AdjustedValue, double Threshold, DateTimeOffset? LastActiveUtc, bool Interrupted) start)
    {
        double? durationMinutes = null;
        if (start.Interrupted && start.LastActiveUtc is { } lastActive)
        {
            durationMinutes = (lastActive - start.Timestamp).TotalMinutes;
        }

        return new AlertEpisodeRow(
            start.Id, start.Timestamp, start.Metric, start.DriveName, durationMinutes, start.Interrupted,
            start.RawValue, start.AdjustedValue, start.Threshold);
    }

    public List<ResourceSample> GetSamplesForAlertEvent(string databasePath, long alertEventId)
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

    public List<ProcessSnapshotRow> GetProcessSnapshotsForAlertEvent(string databasePath, long alertEventId)
    {
        if (!File.Exists(databasePath))
        {
            return new List<ProcessSnapshotRow>();
        }

        // Garante que um banco de uma versão anterior do app (sem IoKbPerSec) já esteja
        // migrado antes de consultar essa coluna — a conexão abaixo é somente leitura.
        PermanentDatabase.EnsureSchema(databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Kind, ProcessName, ProcessId, CpuPercent, RamMb, IoKbPerSec
            FROM AlertProcessSnapshots
            WHERE AlertEventId = $alertEventId
            ORDER BY Kind, CpuPercent DESC;
            """;
        command.Parameters.AddWithValue("$alertEventId", alertEventId);

        var results = new List<ProcessSnapshotRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ProcessSnapshotRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5)));
        }

        return results;
    }

    public List<DailyAggregateRow> GetDailyAggregates(string databasePath, DateOnly? from, DateOnly? to)
    {
        if (!File.Exists(databasePath))
        {
            return new List<DailyAggregateRow>();
        }

        // Garante que um banco de uma versão anterior do app (sem a tabela DailyAggregates)
        // já esteja migrado antes de consultar — a conexão abaixo é somente leitura.
        PermanentDatabase.EnsureSchema(databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Date, SampleCount, CpuRawSum, RamRawSum, IoPercentSum, DiskFreePercentSum, SystemDrive
            FROM DailyAggregates
            WHERE ($from IS NULL OR Date >= $from) AND ($to IS NULL OR Date <= $to)
            ORDER BY Date;
            """;
        command.Parameters.AddWithValue("$from", (object?)from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);

        var results = new List<DailyAggregateRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sampleCount = reader.GetInt32(1);
            results.Add(new DailyAggregateRow(
                DateOnly.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                sampleCount,
                reader.GetDouble(2) / sampleCount,
                reader.GetDouble(3) / sampleCount,
                reader.GetDouble(4) / sampleCount,
                reader.GetDouble(5) / sampleCount,
                reader.GetString(6)));
        }

        return results;
    }

    // Normaliza pra UTC antes de formatar — TimestampUtc no banco está sempre em +00:00,
    // e a comparação no SQL é textual (TEXT), então um filtro construído com offset local
    // (ex: -03:00, quando o DatePicker manda um DateTime "Unspecified") compararia strings
    // com offsets diferentes e podia dar resultado errado perto da virada do dia.
    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
