using Microsoft.Data.Sqlite;
using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Core.Tests;

public class RecentSamplesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static ResourceSample Sample(int second) =>
        new(T0.AddSeconds(second), 10, 10, 20, 20, 16, 8, new[] { new DiskSample("C:\\", 50, 100, 200, 1) });

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Buffer_descarta_o_que_passou_da_janela()
    {
        var buffer = new RecentSamplesBuffer(TimeSpan.FromSeconds(10));
        for (var second = 0; second <= 30; second += 5)
        {
            buffer.Add(Sample(second));
        }

        // Última em 30 s, janela de 10 s: ficam 20, 25 e 30.
        Assert.Equal(3, buffer.Count);
        Assert.Equal(T0.AddSeconds(20), buffer.GetRange(T0, T0.AddSeconds(60))[0].Timestamp);
    }

    [Fact]
    public void Buffer_devolve_o_intervalo_com_limites_inclusivos()
    {
        var buffer = new RecentSamplesBuffer(TimeSpan.FromMinutes(5));
        for (var second = 0; second <= 30; second += 5)
        {
            buffer.Add(Sample(second));
        }

        var range = buffer.GetRange(T0.AddSeconds(10), T0.AddSeconds(20));

        Assert.Equal(new[] { 10.0, 15, 20 }, range.Select(s => (s.Timestamp - T0).TotalSeconds));
    }

    [Fact]
    public void Captura_so_grava_a_janela_depois_que_ela_termina()
    {
        using var folder = new TempFolder();
        var databasePath = folder.File("test.db");
        var settings = new MonitorSettings { PreEventSeconds = 10, PostEventSeconds = 10, SampleIntervalSeconds = 5 };
        var buffer = new RecentSamplesBuffer(TimeSpan.FromSeconds(30));
        var coordinator = new EventCaptureCoordinator(settings);

        using (var database = new PermanentDatabase(databasePath))
        {
            var alertId = database.InsertAlertEvent(
                new AlertEvent(T0.AddSeconds(30), AlertEventType.Start, "CPU", 95, 95, 90, null));

            for (var second = 0; second <= 35; second += 5)
            {
                buffer.Add(Sample(second));
            }

            // Pico em 30 s: janela de 20 a 40 s. Em 35 s ainda não terminou.
            coordinator.BeginCapture(alertId, T0.AddSeconds(30));
            coordinator.FlushReady(T0.AddSeconds(35), buffer, database);
            Assert.Equal(0, Scalar(databasePath, "SELECT COUNT(*) FROM Samples;"));

            buffer.Add(Sample(40));
            coordinator.FlushReady(T0.AddSeconds(40), buffer, database);
        }

        // 20, 25, 30, 35 e 40 s, cada uma com seu disco.
        Assert.Equal(5, Scalar(databasePath, "SELECT COUNT(*) FROM Samples;"));
        Assert.Equal(5, Scalar(databasePath, "SELECT COUNT(*) FROM DiskSamples;"));
    }

    [Fact]
    public void Encerramento_grava_capturas_pendentes_com_o_que_houver()
    {
        using var folder = new TempFolder();
        var databasePath = folder.File("test.db");
        var settings = new MonitorSettings { PreEventSeconds = 10, PostEventSeconds = 60, SampleIntervalSeconds = 5 };
        var buffer = new RecentSamplesBuffer(TimeSpan.FromSeconds(90));
        var coordinator = new EventCaptureCoordinator(settings);

        using (var database = new PermanentDatabase(databasePath))
        {
            var alertId = database.InsertAlertEvent(
                new AlertEvent(T0.AddSeconds(10), AlertEventType.Start, "RAM", 95, 95, 85, null));
            for (var second = 0; second <= 15; second += 5)
            {
                buffer.Add(Sample(second));
            }

            coordinator.BeginCapture(alertId, T0.AddSeconds(10));
            coordinator.FlushAll(buffer, database);
        }

        // Janela de 0 a 70 s, mas o monitoramento parou em 15 s: grava 0, 5, 10 e 15.
        Assert.Equal(4, Scalar(databasePath, "SELECT COUNT(*) FROM Samples;"));
    }
}
