using System.Globalization;
using Microsoft.Data.Sqlite;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Storage;

// Cache volátil em memória: recebe uma linha por tick e é podado continuamente.
// Some junto com a conexão quando o app encerra — não é persistência, é só a janela
// de onde copiamos amostras pra base permanente quando um alerta dispara.
// InsertSample/Prune são chamados pela thread do loop de monitoramento; GetSamplesInRange
// também é chamado pela UI thread da GUI pra ler "dados correntes" ao vivo. Uma SqliteConnection
// não é segura pra uso concorrente sem serialização externa — daí o lock.
public sealed class CacheDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    public CacheDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        CreateSchema();
    }

    private void CreateSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE CacheSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                CpuRawPercent REAL NOT NULL,
                CpuAdjustedPercent REAL NOT NULL,
                RamRawPercent REAL NOT NULL,
                RamAdjustedPercent REAL NOT NULL,
                RamTotalGb REAL NOT NULL,
                RamAvailableGb REAL NOT NULL
            );
            CREATE INDEX idx_cachesamples_timestamp ON CacheSamples(TimestampUtc);

            CREATE TABLE CacheDiskSamples (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CacheSampleId INTEGER NOT NULL REFERENCES CacheSamples(Id),
                DriveName TEXT NOT NULL,
                FreePercent REAL NOT NULL,
                FreeGb REAL NOT NULL,
                TotalGb REAL NOT NULL,
                IoPercent REAL NOT NULL
            );
            CREATE INDEX idx_cachedisksamples_sample ON CacheDiskSamples(CacheSampleId);
            """;
        command.ExecuteNonQuery();
    }

    public void InsertSample(ResourceSample sample)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();

            using var insertSample = _connection.CreateCommand();
            insertSample.Transaction = transaction;
            insertSample.CommandText = """
                INSERT INTO CacheSamples
                    (TimestampUtc, CpuRawPercent, CpuAdjustedPercent, RamRawPercent, RamAdjustedPercent, RamTotalGb, RamAvailableGb)
                VALUES
                    ($timestamp, $cpuRaw, $cpuAdjusted, $ramRaw, $ramAdjusted, $ramTotal, $ramAvailable);
                SELECT last_insert_rowid();
                """;
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
                    INSERT INTO CacheDiskSamples (CacheSampleId, DriveName, FreePercent, FreeGb, TotalGb, IoPercent)
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

            transaction.Commit();
        }
    }

    public void Prune(DateTimeOffset cutoffUtc)
    {
        lock (_lock)
        {
            var cutoff = FormatTimestamp(cutoffUtc);

            using var transaction = _connection.BeginTransaction();

            using (var deleteDisks = _connection.CreateCommand())
            {
                deleteDisks.Transaction = transaction;
                deleteDisks.CommandText = """
                    DELETE FROM CacheDiskSamples
                    WHERE CacheSampleId IN (SELECT Id FROM CacheSamples WHERE TimestampUtc < $cutoff);
                    """;
                deleteDisks.Parameters.AddWithValue("$cutoff", cutoff);
                deleteDisks.ExecuteNonQuery();
            }

            using (var deleteSamples = _connection.CreateCommand())
            {
                deleteSamples.Transaction = transaction;
                deleteSamples.CommandText = "DELETE FROM CacheSamples WHERE TimestampUtc < $cutoff;";
                deleteSamples.Parameters.AddWithValue("$cutoff", cutoff);
                deleteSamples.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public List<ResourceSample> GetSamplesInRange(DateTimeOffset start, DateTimeOffset end)
    {
        lock (_lock)
        {
            var sampleRows = new List<(long Id, DateTimeOffset Timestamp, double CpuRaw, double CpuAdjusted,
                double RamRaw, double RamAdjusted, double RamTotal, double RamAvailable)>();

            using (var selectSamples = _connection.CreateCommand())
            {
                selectSamples.CommandText = """
                    SELECT Id, TimestampUtc, CpuRawPercent, CpuAdjustedPercent, RamRawPercent, RamAdjustedPercent, RamTotalGb, RamAvailableGb
                    FROM CacheSamples
                    WHERE TimestampUtc BETWEEN $start AND $end
                    ORDER BY TimestampUtc;
                    """;
                selectSamples.Parameters.AddWithValue("$start", FormatTimestamp(start));
                selectSamples.Parameters.AddWithValue("$end", FormatTimestamp(end));

                using var reader = selectSamples.ExecuteReader();
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
                using (var selectDisks = _connection.CreateCommand())
                {
                    selectDisks.CommandText = """
                        SELECT DriveName, FreePercent, FreeGb, TotalGb, IoPercent
                        FROM CacheDiskSamples
                        WHERE CacheSampleId = $sampleId;
                        """;
                    selectDisks.Parameters.AddWithValue("$sampleId", row.Id);

                    using var reader = selectDisks.ExecuteReader();
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
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public void Dispose()
    {
        _connection.Dispose();
    }
}
