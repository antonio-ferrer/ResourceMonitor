using ResourceMonitor.Configuration;
using ResourceMonitor.Diagnostics;

namespace ResourceMonitor.Core.Tests;

// ErrorLog é estático (um arquivo por processo): os testes que mexem nele não rodam em paralelo.
[CollectionDefinition(nameof(ErrorLogCollection), DisableParallelization = true)]
public class ErrorLogCollection;

[Collection(nameof(ErrorLogCollection))]
public class AppSettingsStoreTests
{
    [Fact]
    public void Grava_e_le_de_volta_sem_deixar_tmp()
    {
        using var folder = new TempFolder();
        var path = folder.File("appsettings.json");
        var settings = new MonitorSettings { SampleIntervalSeconds = 7, Thresholds = { CpuPercent = 75 } };

        AppSettingsStore.Save(settings, path);
        var loaded = AppSettingsStore.Load(path);

        Assert.Equal(7, loaded.SampleIntervalSeconds);
        Assert.Equal(75, loaded.Thresholds.CpuPercent);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Arquivo_inexistente_cria_com_os_padroes()
    {
        using var folder = new TempFolder();
        var path = folder.File("appsettings.json");

        var loaded = AppSettingsStore.Load(path);

        Assert.Equal(new MonitorSettings().SampleIntervalSeconds, loaded.SampleIntervalSeconds);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Json_quebrado_vira_padroes_e_o_original_fica_guardado()
    {
        using var folder = new TempFolder();
        ErrorLog.Initialize(folder.Path);
        var path = folder.File("appsettings.json");
        File.WriteAllText(path, "{ \"SampleIntervalSeconds\": 5, ");

        var loaded = AppSettingsStore.Load(path);

        Assert.Equal(new MonitorSettings().SampleIntervalSeconds, loaded.SampleIntervalSeconds);
        Assert.Equal("{ \"SampleIntervalSeconds\": 5, ", File.ReadAllText(path + ".invalid"));
        Assert.Contains("appsettings.json inválido", File.ReadAllText(folder.File("errors.log")));
        Assert.Equal(5, AppSettingsStore.Load(path).SampleIntervalSeconds);
    }

    [Fact]
    public void Valores_impossiveis_sao_corrigidos_ao_carregar()
    {
        using var folder = new TempFolder();
        var path = folder.File("appsettings.json");
        File.WriteAllText(path, """
            { "SampleIntervalSeconds": 0, "ConsecutiveBreachesToAlert": -2, "ConsecutiveRecoveriesToClear": 0,
              "TopProcessCount": 0, "PreEventSeconds": -10, "PostEventSeconds": -1, "LogDirectory": "",
              "ExcludedProcesses": null, "Thresholds": null }
            """);

        var loaded = AppSettingsStore.Load(path);

        Assert.Equal(1, loaded.SampleIntervalSeconds);
        Assert.Equal(1, loaded.ConsecutiveBreachesToAlert);
        Assert.Equal(1, loaded.ConsecutiveRecoveriesToClear);
        Assert.Equal(1, loaded.TopProcessCount);
        Assert.Equal(0, loaded.PreEventSeconds);
        Assert.Equal(0, loaded.PostEventSeconds);
        Assert.Equal("logs", loaded.LogDirectory);
        Assert.NotNull(loaded.ExcludedProcesses);
        Assert.NotNull(loaded.Thresholds.DiskFreeThresholds);
    }
}

[Collection(nameof(ErrorLogCollection))]
public class ErrorLogTests
{
    [Fact]
    public void Grava_mensagem_e_excecao_completa()
    {
        using var folder = new TempFolder();
        ErrorLog.Initialize(folder.Path);

        ErrorLog.Write("Teste", "algo falhou", new InvalidOperationException("detalhe"));

        var text = File.ReadAllText(folder.File("errors.log"));
        Assert.Contains("[Teste] algo falhou", text);
        Assert.Contains("InvalidOperationException: detalhe", text);
    }

    [Fact]
    public void Gira_pra_ponto_1_ao_passar_de_1_mb()
    {
        using var folder = new TempFolder();
        ErrorLog.Initialize(folder.Path);
        File.WriteAllText(folder.File("errors.log"), new string('x', 1024 * 1024 + 1));

        ErrorLog.Write("Teste", "depois de girar");

        Assert.True(new FileInfo(folder.File("errors.log.1")).Length > 1024 * 1024);
        Assert.Contains("depois de girar", File.ReadAllText(folder.File("errors.log")));
        Assert.True(new FileInfo(folder.File("errors.log")).Length < 1024);
    }

    [Fact]
    public void Pasta_impossivel_nao_lanca_excecao()
    {
        using var folder = new TempFolder();
        File.WriteAllText(folder.File("arquivo"), "um arquivo no lugar da pasta");
        ErrorLog.Initialize(Path.Combine(folder.File("arquivo"), "logs"));

        ErrorLog.Write("Teste", "não pode quebrar");
    }
}
