using Microsoft.Data.Sqlite;
using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Core.Tests;

public class RetentionTests
{
    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    // Um episódio completo como o loop grava: Start + processos + janela de amostras + End.
    private static void InsertEpisode(PermanentDatabase database, DateTimeOffset start)
    {
        var startId = database.InsertAlertEvent(new AlertEvent(start, AlertEventType.Start, "CPU", 95, 95, 90, null));
        database.InsertProcessSnapshots(startId, "Cpu", new[] { new ProcessUsage("app", 1, 50, 100, 0) });
        database.InsertSampleWindow(startId, new[]
        {
            new ResourceSample(start, 95, 95, 40, 40, 16, 8, new[] { new DiskSample("C:\\", 50, 100, 200, 1) }),
            new ResourceSample(start.AddSeconds(5), 96, 96, 40, 40, 16, 8, new[] { new DiskSample("C:\\", 50, 100, 200, 1) }),
        });
        database.InsertAlertEvent(new AlertEvent(start.AddMinutes(2), AlertEventType.End, "CPU", 20, 20, 90, null));
    }

    [Fact]
    public void Apaga_so_os_picos_antigos_e_mantem_a_tendencia()
    {
        using var folder = new TempFolder();
        var databasePath = folder.File("test.db");
        var now = DateTimeOffset.UtcNow;

        int purged;
        using (var database = new PermanentDatabase(databasePath))
        {
            InsertEpisode(database, now.AddDays(-400));
            InsertEpisode(database, now.AddDays(-10));
            database.UpsertDailyAggregate(DateOnly.FromDateTime(now.AddDays(-400).Date), 10, 40, 1, 60, "C:\\");

            purged = database.PurgeAlertsOlderThan(now.AddDays(-365));
        }

        Assert.Equal(1, purged);
        Assert.Equal(2, Scalar(databasePath, "SELECT COUNT(*) FROM AlertEvents;"));
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM AlertProcessSnapshots;"));
        Assert.Equal(2, Scalar(databasePath, "SELECT COUNT(*) FROM Samples;"));
        Assert.Equal(2, Scalar(databasePath, "SELECT COUNT(*) FROM DiskSamples;"));
        Assert.Equal(1, Scalar(databasePath, "SELECT COUNT(*) FROM DailyAggregates;"));
        Assert.Single(new AlertEventQueries(new NullTraceLogger()).GetAlertEpisodes(databasePath, null, null));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(365, 1)]
    public void Retencao_zero_guarda_para_sempre(int retentionDays, long expectedStarts)
    {
        using var folder = new TempFolder();
        var databasePath = folder.File("test.db");

        using (var database = new PermanentDatabase(databasePath))
        {
            InsertEpisode(database, DateTimeOffset.UtcNow.AddDays(-400));
            InsertEpisode(database, DateTimeOffset.UtcNow.AddDays(-1));
            MonitoringService.PurgeExpiredPeaks(database, new MonitorSettings { PeakRetentionDays = retentionDays });
        }

        Assert.Equal(expectedStarts, Scalar(databasePath, "SELECT COUNT(*) FROM AlertEvents WHERE EventType = 'Start';"));
    }

    [Fact]
    public void Retencao_negativa_vira_zero_ao_carregar()
    {
        Assert.Equal(0, AppSettingsStore.Normalize(new MonitorSettings { PeakRetentionDays = -5 }).PeakRetentionDays);
        Assert.Equal(365, new MonitorSettings().PeakRetentionDays);
    }
}
