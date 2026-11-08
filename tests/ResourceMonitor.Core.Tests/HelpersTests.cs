using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;
using ResourceMonitor.Updates;

namespace ResourceMonitor.Core.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v0.4.1", "0.4.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.4.0", "0.4.0", false)]
    [InlineData("v0.3.0", "0.4.0", false)]
    [InlineData("nao-e-versao", "0.4.0", false)]
    [InlineData("v0.5.0", "lixo", false)]
    public void Compara_tag_do_github_com_a_versao_atual(string latestTag, string currentVersion, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(latestTag, currentVersion));
    }
}

public class CsvExporterTests
{
    [Theory]
    [InlineData("simples", "simples")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("diz \"oi\"", "\"diz \"\"oi\"\"\"")]
    [InlineData("linha1\nlinha2", "\"linha1\nlinha2\"")]
    public void Escapa_conforme_csv(string value, string expected)
    {
        Assert.Equal(expected, CsvExporter.Escape(value));
    }
}

public class ResourceSamplerTests
{
    [Theory]
    [InlineData("ResourceMonitor.Gui", true)]
    [InlineData("resourcemonitor", true)]
    [InlineData("chrome", false)]
    public void Padrao_de_exclusao_aceita_curinga_sem_diferenciar_maiusculas(string processName, bool expected)
    {
        Assert.Equal(expected, ResourceSampler.MatchesAnyPattern(processName, ["ResourceMonitor*"]));
    }

    // explorer (10) → chrome (20); ResourceMonitor.Gui (30) → msedgewebview2 (31) → msedgewebview2 (32)
    private static readonly Dictionary<int, int> ParentOf = new() { [20] = 10, [31] = 30, [32] = 31 };
    private static readonly Dictionary<int, string> NameOf = new()
    {
        [10] = "explorer", [20] = "chrome", [30] = "ResourceMonitor.Gui", [31] = "msedgewebview2", [32] = "msedgewebview2",
    };

    private static ProcessUsage Usage(int pid) => new(NameOf[pid], pid, 0, 0, 0);

    [Fact]
    public void Processo_comum_fica_com_o_proprio_nome()
    {
        Assert.Equal("chrome", ResourceSampler.ResolveAttributionName(Usage(20), ParentOf, NameOf));
    }

    [Fact]
    public void Webview2_conta_no_app_que_o_abriu_mesmo_em_cadeia()
    {
        Assert.Equal("ResourceMonitor.Gui", ResourceSampler.ResolveAttributionName(Usage(31), ParentOf, NameOf));
        Assert.Equal("ResourceMonitor.Gui", ResourceSampler.ResolveAttributionName(Usage(32), ParentOf, NameOf));
    }

    [Fact]
    public void Webview2_sem_pai_conhecido_mantem_o_proprio_nome()
    {
        var orphan = new ProcessUsage("msedgewebview2", 99, 0, 0, 0);
        Assert.Equal("msedgewebview2", ResourceSampler.ResolveAttributionName(orphan, ParentOf, NameOf));
    }
}

// Integração: lê a tabela de processos de verdade do Windows (sem admin).
public class SystemProcessSnapshotTests
{
    [Fact]
    public void Le_o_proprio_processo_e_dezenas_de_outros()
    {
        var entries = new SystemProcessSnapshot().Read();

        Assert.True(entries.Count > 20, $"só {entries.Count} processos lidos");
        var self = Assert.Single(entries, e => e.Pid == Environment.ProcessId);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName, self.Name, ignoreCase: true);
        Assert.True(self.WorkingSetBytes > 0);
        Assert.Contains(entries, e => e.Pid == 4 && e.Name == "System");
    }
}
