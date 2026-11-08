using ResourceMonitor.Configuration;
using ResourceMonitor.Diagnostics;

namespace ResourceMonitor.Core.Tests;

[Collection(nameof(ErrorLogCollection))]
public class MonitoringStateStoreTests
{
    [Fact]
    public void Primeira_execucao_comeca_monitorando()
    {
        using var folder = new TempFolder();

        Assert.True(MonitoringStateStore.LoadMonitoringActive(folder.File("state.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lembra_a_ultima_escolha(bool active)
    {
        using var folder = new TempFolder();
        var path = folder.File("state.json");

        MonitoringStateStore.SaveMonitoringActive(active, path);

        Assert.Equal(active, MonitoringStateStore.LoadMonitoringActive(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("{ quebrado")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Arquivo_ilegivel_ou_incompleto_volta_a_monitorar(string content)
    {
        using var folder = new TempFolder();
        ErrorLog.Initialize(folder.Path);
        var path = folder.File("state.json");
        File.WriteAllText(path, content);

        Assert.True(MonitoringStateStore.LoadMonitoringActive(path));
    }

    [Fact]
    public void Falha_ao_gravar_nao_lanca_excecao()
    {
        using var folder = new TempFolder();
        ErrorLog.Initialize(folder.Path);
        File.WriteAllText(folder.File("arquivo"), "um arquivo no lugar da pasta");

        MonitoringStateStore.SaveMonitoringActive(false, Path.Combine(folder.File("arquivo"), "state.json"));

        Assert.Contains("state.json", File.ReadAllText(folder.File("errors.log")));
    }
}
