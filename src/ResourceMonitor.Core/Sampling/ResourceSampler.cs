using System.Text.RegularExpressions;

namespace ResourceMonitor.Sampling;

public sealed record ProcessUsage(string Name, int Id, double CpuPercent, double RamMb, double IoKbPerSec);

// Mesmo nome, várias instâncias somadas (ver GetTopProcessesGrouped) — InstanceCount > 1
// avisa que o valor é a soma de mais de um processo, não um único PID.
public sealed record GroupedProcessUsage(string Name, int InstanceCount, double CpuPercent, double RamMb, double IoKbPerSec);

public sealed class ResourceSampler
{
    private readonly DiskMonitor _diskMonitor;
    private readonly SystemProcessSnapshot _processSnapshot = new();
    private Dictionary<int, ProcessCounters> _lastCounters = new();
    private Dictionary<int, int> _lastParentOf = new();
    private IReadOnlyList<ProcessUsage> _lastAllProcessUsages = Array.Empty<ProcessUsage>();

    private SystemMetricsReader.CpuTimesSnapshot? _lastSystemCpuTimes;
    private DateTimeOffset? _lastSampleTime;

    public ResourceSampler(DiskMonitor diskMonitor)
    {
        _diskMonitor = diskMonitor;
    }

    // Retorna null na primeira chamada (amostra de aquecimento, ainda sem delta pra calcular).
    public ResourceSample? Sample(IReadOnlyList<string> excludedProcessPatterns)
    {
        var now = DateTimeOffset.UtcNow;
        var cpuTimes = SystemMetricsReader.ReadCpuTimes();

        // Delta real entre este tick e o anterior (null no tick de aquecimento) — usado tanto
        // pro CPU do sistema quanto pro CPU%/I/O por processo em WalkProcesses, no lugar da
        // antiga janela fixa de 500ms que o ProcessSnapshotter usava só na hora do alerta.
        double? elapsedSeconds = _lastSampleTime is { } previousSampleTime
            ? (now - previousSampleTime).TotalSeconds
            : null;

        // Roda sempre (mesmo no aquecimento) pra começar a rastrear todo processo um tick mais
        // cedo — antes, isso só acontecia dentro do guard abaixo, perdendo o primeiro tick.
        var (excludedCpuSeconds, excludedRamBytes) = WalkProcesses(excludedProcessPatterns, elapsedSeconds);

        ResourceSample? result = null;

        if (_lastSystemCpuTimes is { } previousCpuTimes && elapsedSeconds is { } seconds && seconds > 0)
        {
            var idleDelta = cpuTimes.IdleTicks - previousCpuTimes.IdleTicks;
            var kernelDelta = cpuTimes.KernelTicks - previousCpuTimes.KernelTicks;
            var userDelta = cpuTimes.UserTicks - previousCpuTimes.UserTicks;
            var totalDelta = kernelDelta + userDelta;

            var cpuRawPercent = totalDelta > 0
                ? Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100)
                : 0;

            var excludedCpuPercent = Math.Clamp(
                excludedCpuSeconds / (seconds * Environment.ProcessorCount) * 100.0, 0, 100);

            var cpuAdjustedPercent = Math.Max(0, cpuRawPercent - excludedCpuPercent);

            var memoryInfo = SystemMetricsReader.ReadMemoryInfo();
            var excludedRamPercent = memoryInfo.TotalPhysBytes > 0
                ? (double)excludedRamBytes / memoryInfo.TotalPhysBytes * 100.0
                : 0;
            var ramAdjustedPercent = Math.Max(0, memoryInfo.PercentUsed - excludedRamPercent);

            var disks = _diskMonitor.SampleDisks();

            result = new ResourceSample(
                now,
                cpuRawPercent,
                cpuAdjustedPercent,
                memoryInfo.PercentUsed,
                ramAdjustedPercent,
                memoryInfo.TotalPhysBytes / 1024.0 / 1024.0 / 1024.0,
                memoryInfo.AvailPhysBytes / 1024.0 / 1024.0 / 1024.0,
                disks);
        }

        _lastSystemCpuTimes = cpuTimes;
        _lastSampleTime = now;

        return result;
    }

    // Snapshot dos processos que mais consumiram CPU/RAM/I/O, calculado a partir do último tick
    // (não uma captura dedicada) — leitura em memória, sem enumerar processos de novo.
    public (IReadOnlyList<ProcessUsage> TopByCpu, IReadOnlyList<ProcessUsage> TopByRam, IReadOnlyList<ProcessUsage> TopByIo)
        GetTopProcesses(int topN) => (
        _lastAllProcessUsages.OrderByDescending(u => u.CpuPercent).Take(topN).ToList(),
        _lastAllProcessUsages.OrderByDescending(u => u.RamMb).Take(topN).ToList(),
        _lastAllProcessUsages.OrderByDescending(u => u.IoKbPerSec).Take(topN).ToList());

    // Mesma ideia, mas somando por nome antes de ranquear — apps tipo Electron/Chromium sobem
    // vários processos com o mesmo nome (main, renderer, GPU...), o que faria o mesmo app
    // ocupar sozinho várias posições do top-N. Cada processo conta no próprio nome; a exceção
    // são os hospedeiros de conteúdo embutido (ver EmbeddedHostNames), contabilizados no app
    // que os abriu. Só usada pro monitor ao vivo (LiveMonitorService); o snapshot gravado no
    // alerta (GetTopProcesses acima) continua por PID — lá o detalhe de qual processo
    // específico importa pra investigação.
    public (IReadOnlyList<GroupedProcessUsage> TopByCpu, IReadOnlyList<GroupedProcessUsage> TopByRam, IReadOnlyList<GroupedProcessUsage> TopByIo)
        GetTopProcessesGrouped(int topN)
    {
        var nameOf = new Dictionary<int, string>();
        foreach (var usage in _lastAllProcessUsages)
        {
            nameOf[usage.Id] = usage.Name;
        }

        var grouped = _lastAllProcessUsages
            .GroupBy(u => ResolveAttributionName(u, _lastParentOf, nameOf), StringComparer.OrdinalIgnoreCase)
            .Select(g => new GroupedProcessUsage(
                g.Key,
                g.Count(),
                g.Sum(u => u.CpuPercent),
                g.Sum(u => u.RamMb),
                g.Sum(u => u.IoKbPerSec)))
            .ToList();

        return (
            grouped.OrderByDescending(u => u.CpuPercent).Take(topN).ToList(),
            grouped.OrderByDescending(u => u.RamMb).Take(topN).ToList(),
            grouped.OrderByDescending(u => u.IoKbPerSec).Take(topN).ToList());
    }

    // Processos "carona": sobem como filhos de outro app pra renderizar conteúdo embutido (o
    // WebView2 dos gráficos do próprio ResourceMonitor, do Teams, do Outlook novo...). Esses são
    // atribuídos ao app dono — qualquer outro processo fica com o próprio nome, senão tudo que
    // o Explorer abre (Chrome, VS Code...) viraria "explorer".
    private static readonly HashSet<string> EmbeddedHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedgewebview2",
    };

    // Pra um hospedeiro embutido, sobe a cadeia de pais de mesmo nome
    // ("msedgewebview2 → msedgewebview2 → ResourceMonitor.Gui") e devolve o primeiro ancestral
    // de nome diferente. Se a cadeia nunca muda de nome (ou o pai não é resolvível), mantém o
    // nome original.
    private const int MaxAncestorDepth = 32;

    internal static string ResolveAttributionName(ProcessUsage usage, Dictionary<int, int> parentOf, Dictionary<int, string> nameOf)
    {
        if (!EmbeddedHostNames.Contains(usage.Name))
        {
            return usage.Name;
        }

        var currentId = usage.Id;
        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            if (!parentOf.TryGetValue(currentId, out var parentId) || !nameOf.TryGetValue(parentId, out var parentName))
            {
                break;
            }

            if (!string.Equals(parentName, usage.Name, StringComparison.OrdinalIgnoreCase))
            {
                return parentName;
            }

            currentId = parentId;
        }

        return usage.Name;
    }

    // Uma única enumeração de todo processo por tick (ver SystemProcessSnapshot), reaproveitada
    // pra duas coisas: somar CPU/RAM de quem bate os padrões de exclusão (pro cálculo de uso
    // "líquido") e manter _lastAllProcessUsages atualizado (pra servir GetTopProcesses e
    // GetTopProcessesGrouped sem nova varredura).
    private (double CpuSeconds, long RamBytes) WalkProcesses(IReadOnlyList<string> excludedPatterns, double? elapsedSeconds)
    {
        double excludedCpuSeconds = 0;
        long excludedRamBytes = 0;

        var entries = _processSnapshot.Read();
        var counters = new Dictionary<int, ProcessCounters>(entries.Count);
        var parentOf = new Dictionary<int, int>(entries.Count);
        var usages = new List<ProcessUsage>(entries.Count);
        var seconds = elapsedSeconds is { } elapsed && elapsed > 0 ? elapsed : (double?)null;

        foreach (var entry in entries)
        {
            if (entry.Pid == 0)
            {
                continue; // "Idle": tempo ocioso da CPU, não um processo de verdade
            }

            counters[entry.Pid] = new ProcessCounters(entry.CreateTime, entry.CpuTicks, entry.IoBytes);
            parentOf[entry.Pid] = entry.ParentPid;

            var isExcluded = excludedPatterns.Count > 0 && MatchesAnyPattern(entry.Name, excludedPatterns);

            double cpuPercent = 0;
            double ioKbPerSec = 0;

            // Delta só contra o MESMO processo (PID + horário de criação): um PID reaproveitado
            // por outro processo entre dois ticks não vira um delta sem sentido.
            if (seconds is { } s
                && _lastCounters.TryGetValue(entry.Pid, out var previous)
                && previous.CreateTime == entry.CreateTime)
            {
                var cpuDeltaSeconds = Math.Max(0, entry.CpuTicks - previous.CpuTicks) / (double)TimeSpan.TicksPerSecond;
                cpuPercent = Math.Clamp(cpuDeltaSeconds / (s * Environment.ProcessorCount) * 100.0, 0, 100);
                if (isExcluded)
                {
                    excludedCpuSeconds += cpuDeltaSeconds;
                }

                var ioBytesDelta = entry.IoBytes >= previous.IoBytes ? entry.IoBytes - previous.IoBytes : 0;
                ioKbPerSec = ioBytesDelta / s / 1024.0;
            }

            if (isExcluded)
            {
                excludedRamBytes += entry.WorkingSetBytes;
            }

            usages.Add(new ProcessUsage(entry.Name, entry.Pid, cpuPercent, entry.WorkingSetBytes / 1024.0 / 1024.0, ioKbPerSec));
        }

        // Troca as referências em vez de remover entradas antigas — processo que sumiu
        // simplesmente não está no dicionário novo.
        _lastCounters = counters;
        _lastParentOf = parentOf;
        _lastAllProcessUsages = usages;

        return (excludedCpuSeconds, excludedRamBytes);
    }

    internal static bool MatchesAnyPattern(string processName, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (WildcardMatch(processName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    private readonly record struct ProcessCounters(long CreateTime, long CpuTicks, ulong IoBytes);
}
