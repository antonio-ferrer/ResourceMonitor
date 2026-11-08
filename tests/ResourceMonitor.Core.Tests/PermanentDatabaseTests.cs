using Microsoft.Data.Sqlite;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Core.Tests;

public class PermanentDatabaseTests
{
    private static object? Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void Banco_novo_fica_em_modo_wal()
    {
        using var folder = new TempFolder();
        var databasePath = folder.CreateDatabase();

        Assert.Equal("wal", Scalar(databasePath, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void Templates_padrao_nao_duplicam_ao_abrir_de_novo()
    {
        using var folder = new TempFolder();
        var databasePath = folder.CreateDatabase();

        using (new PermanentDatabase(databasePath))
        {
        }

        PermanentDatabase.EnsureSchema(databasePath);
        PermanentDatabase.EnsureSchema(databasePath);

        Assert.Equal(3L, Scalar(databasePath, "SELECT COUNT(*) FROM Templates WHERE IsBuiltIn = 1;"));
        Assert.Equal(3, new TemplateQueries(new NullTraceLogger()).GetAll(databasePath).Count);
    }

    [Fact]
    public void Agregado_diario_acumula_as_amostras_do_mesmo_dia()
    {
        using var folder = new TempFolder();
        var databasePath = folder.File("test.db");
        var day = new DateOnly(2026, 11, 8);

        using (var database = new PermanentDatabase(databasePath))
        {
            database.UpsertDailyAggregate(day, cpuRawPercent: 10, ramRawPercent: 40, ioPercent: 2, diskFreePercent: 70, "C:\\");
            database.UpsertDailyAggregate(day, cpuRawPercent: 30, ramRawPercent: 60, ioPercent: 4, diskFreePercent: 68, "C:\\");
        }

        var row = Assert.Single(new AlertEventQueries(new NullTraceLogger()).GetDailyAggregates(databasePath, day, day));
        Assert.Equal(20, row.AvgCpuRawPercent, precision: 6);
        Assert.Equal(50, row.AvgRamRawPercent, precision: 6);
        Assert.Equal(3, row.AvgIoPercent, precision: 6);
        Assert.Equal(69, row.AvgDiskFreePercent, precision: 6);
    }

    [Fact]
    public void Template_do_usuario_e_criado_alterado_e_excluido()
    {
        using var folder = new TempFolder();
        var databasePath = folder.CreateDatabase();
        var queries = new TemplateQueries(new NullTraceLogger());

        var id = PermanentDatabase.InsertTemplate(databasePath, "Meu", "SELECT 1");
        PermanentDatabase.UpdateTemplate(databasePath, id, "Meu", "SELECT 2");
        Assert.Equal("SELECT 2", queries.GetAll(databasePath).Single(t => t.Id == id).Command);

        PermanentDatabase.DeleteTemplate(databasePath, id);
        Assert.DoesNotContain(queries.GetAll(databasePath), t => t.Id == id);
    }
}
