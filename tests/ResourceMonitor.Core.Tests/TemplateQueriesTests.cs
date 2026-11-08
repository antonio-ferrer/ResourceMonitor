using Microsoft.Data.Sqlite;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Core.Tests;

public class TemplateQueriesTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select * from Templates;")]
    [InlineData("WITH t AS (SELECT 1 AS x) SELECT x FROM t")]
    [InlineData("-- comentário antes\nSELECT 1")]
    [InlineData("/* um ; dentro do comentário */ SELECT 1")]
    public void Aceita_consultas_de_leitura(string sql)
    {
        TemplateQueries.ValidateReadOnly(sql);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- só comentário")]
    [InlineData("INSERT INTO Templates (Name) VALUES ('x')")]
    [InlineData("UPDATE Templates SET Name = 'x'")]
    [InlineData("DELETE FROM Templates")]
    [InlineData("DROP TABLE Templates")]
    [InlineData("ATTACH DATABASE 'x.db' AS x")]
    [InlineData("PRAGMA journal_mode=DELETE")]
    [InlineData("SELECT 1; DELETE FROM Templates")]
    public void Recusa_o_que_nao_e_uma_consulta_unica_de_leitura(string sql)
    {
        Assert.Throws<InvalidOperationException>(() => TemplateQueries.ValidateReadOnly(sql));
    }

    // WITH ... DELETE passa pela checagem de texto (começa com WITH) — a garantia de verdade é a
    // conexão somente-leitura de ExecuteReadOnly.
    [Fact]
    public void Escrita_disfarcada_de_with_e_barrada_pela_conexao_somente_leitura()
    {
        using var folder = new TempFolder();
        var databasePath = folder.CreateDatabase();
        var queries = new TemplateQueries(new NullTraceLogger());

        Assert.Throws<SqliteException>(() => queries.ExecuteReadOnly(
            databasePath, "WITH t AS (SELECT 1) DELETE FROM Templates", DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Equal(3, queries.GetAll(databasePath).Count);
    }
}
