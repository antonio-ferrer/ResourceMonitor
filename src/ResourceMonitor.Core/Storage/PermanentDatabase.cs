using System.Globalization;
using Microsoft.Data.Sqlite;
using ResourceMonitor.Alerting;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Storage;

// Base persistente entre execuções. Só recebe dados quando um alerta dispara:
// o próprio evento, o snapshot de processos, e a janela de amostras em torno do pico
// (copiada do CacheDatabase pelo EventCaptureCoordinator).
public sealed class PermanentDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public PermanentDatabase(string databaseFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseFilePath)!);
        _connection = new SqliteConnection($"Data Source={databaseFilePath}");
        _connection.Open();
        ApplySchema(_connection);
    }

    // Chamado pelo lado de leitura (AlertEventQueries) antes de abrir sua própria conexão
    // somente-leitura: garante que um banco criado por uma versão antiga do app (antes de
    // colunas como LastActiveUtc/Interrupted existirem) já esteja migrado, sem depender de
    // que o usuário tenha clicado em "Iniciar" nessa execução (o que criaria um PermanentDatabase
    // e migraria de qualquer forma).
    public static void EnsureSchema(string databaseFilePath)
    {
        if (!File.Exists(databaseFilePath))
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={databaseFilePath}");
        connection.Open();
        ApplySchema(connection);
    }

    private static void ApplySchema(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS AlertEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                EventType TEXT NOT NULL,
                Metric TEXT NOT NULL,
                DriveName TEXT NULL,
                RawValue REAL NOT NULL,
                AdjustedValue REAL NULL,
                Threshold REAL NOT NULL,
                LastActiveUtc TEXT NULL,
                Interrupted INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_alertevents_timestamp ON AlertEvents(TimestampUtc);

            CREATE TABLE IF NOT EXISTS AlertProcessSnapshots (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AlertEventId INTEGER NOT NULL REFERENCES AlertEvents(Id),
                Kind TEXT NOT NULL,
                ProcessName TEXT NOT NULL,
                ProcessId INTEGER NOT NULL,
                CpuPercent REAL NOT NULL,
                RamMb REAL NOT NULL,
                IoKbPerSec REAL NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_snapshots_alert ON AlertProcessSnapshots(AlertEventId);

            CREATE TABLE IF NOT EXISTS Samples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AlertEventId INTEGER NOT NULL REFERENCES AlertEvents(Id),
                TimestampUtc TEXT NOT NULL,
                CpuRawPercent REAL NOT NULL,
                CpuAdjustedPercent REAL NOT NULL,
                RamRawPercent REAL NOT NULL,
                RamAdjustedPercent REAL NOT NULL,
                RamTotalGb REAL NOT NULL,
                RamAvailableGb REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_samples_alert ON Samples(AlertEventId);
            CREATE INDEX IF NOT EXISTS idx_samples_timestamp ON Samples(TimestampUtc);

            CREATE TABLE IF NOT EXISTS DiskSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SampleId INTEGER NOT NULL REFERENCES Samples(Id),
                DriveName TEXT NOT NULL,
                FreePercent REAL NOT NULL,
                FreeGb REAL NOT NULL,
                TotalGb REAL NOT NULL,
                IoPercent REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_disksamples_sample ON DiskSamples(SampleId);
            """;
            command.ExecuteNonQuery();
        }

        // CREATE TABLE IF NOT EXISTS não altera uma tabela que já existia antes dessas colunas
        // serem adicionadas — então garante que bancos antigos ganhem as colunas novas também.
        EnsureColumnExists(connection, "AlertEvents", "LastActiveUtc", "TEXT NULL");
        EnsureColumnExists(connection, "AlertEvents", "Interrupted", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(connection, "AlertProcessSnapshots", "IoKbPerSec", "REAL NOT NULL DEFAULT 0");
    }

    private static void EnsureColumnExists(SqliteConnection connection, string table, string column, string columnDefinition)
    {
        var exists = false;
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = $"PRAGMA table_info({table});";
            using var reader = checkCommand.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition};";
            alterCommand.ExecuteNonQuery();
        }
    }

    public long InsertAlertEvent(AlertEvent alertEvent)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AlertEvents (TimestampUtc, EventType, Metric, DriveName, RawValue, AdjustedValue, Threshold)
            VALUES ($timestamp, $eventType, $metric, $driveName, $rawValue, $adjustedValue, $threshold);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(alertEvent.Timestamp));
        command.Parameters.AddWithValue("$eventType", alertEvent.EventType.ToString());
        command.Parameters.AddWithValue("$metric", alertEvent.Metric);
        command.Parameters.AddWithValue("$driveName", (object?)alertEvent.DriveName ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawValue", alertEvent.RawValue);
        command.Parameters.AddWithValue("$adjustedValue", (object?)alertEvent.AdjustedValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$threshold", alertEvent.Threshold);

        return (long)command.ExecuteScalar()!;
    }

    public void InsertProcessSnapshots(long alertEventId, string kind, IReadOnlyList<ProcessUsage> processes)
    {
        if (processes.Count == 0)
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        foreach (var process in processes)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO AlertProcessSnapshots (AlertEventId, Kind, ProcessName, ProcessId, CpuPercent, RamMb, IoKbPerSec)
                VALUES ($alertEventId, $kind, $processName, $processId, $cpuPercent, $ramMb, $ioKbPerSec);
                """;
            command.Parameters.AddWithValue("$alertEventId", alertEventId);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$processName", process.Name);
            command.Parameters.AddWithValue("$processId", process.Id);
            command.Parameters.AddWithValue("$cpuPercent", process.CpuPercent);
            command.Parameters.AddWithValue("$ramMb", process.RamMb);
            command.Parameters.AddWithValue("$ioKbPerSec", process.IoKbPerSec);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void InsertSampleWindow(long alertEventId, IEnumerable<ResourceSample> samples)
    {
        using var transaction = _connection.BeginTransaction();

        foreach (var sample in samples)
        {
            using var insertSample = _connection.CreateCommand();
            insertSample.Transaction = transaction;
            insertSample.CommandText = """
                INSERT INTO Samples
                    (AlertEventId, TimestampUtc, CpuRawPercent, CpuAdjustedPercent, RamRawPercent, RamAdjustedPercent, RamTotalGb, RamAvailableGb)
                VALUES
                    ($alertEventId, $timestamp, $cpuRaw, $cpuAdjusted, $ramRaw, $ramAdjusted, $ramTotal, $ramAvailable);
                SELECT last_insert_rowid();
                """;
            insertSample.Parameters.AddWithValue("$alertEventId", alertEventId);
            insertSample.Parameters.AddWithValue("$timestamp", FormatTimestamp(sample.Timestamp));
            insertSample.Parameters.AddWithValue("$cpuRaw", sample.CpuRawPercent);
            insertSample.Parameters.AddWithValue("$cpuAdjusted", sample.CpuAdjustedPercent);
            insertSample.Parameters.AddWithValue("$ramRaw", sample.RamRawPercent);
            insertSample.Parameters.AddWithValue("$ramAdjusted", sample.RamAdjustedPercent);
            insertSample.Parameters.AddWithValue("$ramTotal", sample.RamTotalGb);
            insertSample.Parameters.AddWithValue("$ramAvailable", sample.RamAvailableGb);

            var sampleId = (long)insertSample.ExecuteScalar()!;

            foreach (var disk in sample.Disks)
            {
                using var insertDisk = _connection.CreateCommand();
                insertDisk.Transaction = transaction;
                insertDisk.CommandText = """
                    INSERT INTO DiskSamples (SampleId, DriveName, FreePercent, FreeGb, TotalGb, IoPercent)
                    VALUES ($sampleId, $driveName, $freePercent, $freeGb, $totalGb, $ioPercent);
                    """;
                insertDisk.Parameters.AddWithValue("$sampleId", sampleId);
                insertDisk.Parameters.AddWithValue("$driveName", disk.DriveName);
                insertDisk.Parameters.AddWithValue("$freePercent", disk.FreePercent);
                insertDisk.Parameters.AddWithValue("$freeGb", disk.FreeGb);
                insertDisk.Parameters.AddWithValue("$totalGb", disk.TotalGb);
                insertDisk.Parameters.AddWithValue("$ioPercent", disk.IoPercent);
                insertDisk.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    // "Heartbeat" chamado a cada tick enquanto o alerta segue ativo — se o app for encerrado
    // sem um End (crash, kill), esse é o último instante confirmado em que o alerta ainda
    // estava de pé, usado como duração mínima conhecida na listagem.
    public void UpdateLastActive(long alertEventId, DateTimeOffset timestamp)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE AlertEvents SET LastActiveUtc = $timestamp WHERE Id = $id;";
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", alertEventId);
        command.ExecuteNonQuery();
    }

    // Chamado no encerramento (Parar manual ou shutdown) pra todo Start que ainda não tinha
    // recebido seu End — marca explicitamente como interrompido, distinto de "ainda monitorando".
    public void MarkInterrupted(long alertEventId, DateTimeOffset timestamp)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE AlertEvents SET Interrupted = 1, LastActiveUtc = $timestamp WHERE Id = $id;";
        command.Parameters.AddWithValue("$timestamp", FormatTimestamp(timestamp));
        command.Parameters.AddWithValue("$id", alertEventId);
        command.ExecuteNonQuery();
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    // Só deve ser chamado com o monitoramento parado — abre sua própria conexão e não
    // coordena com uma instância de PermanentDatabase que porventura já esteja escrevendo.
    public static void ClearAllData(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM DiskSamples;
            DELETE FROM Samples;
            DELETE FROM AlertProcessSnapshots;
            DELETE FROM AlertEvents;
            VACUUM;
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
