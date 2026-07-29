using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ResourceMonitor.Sampling;

public sealed record ProcessUsage(string Name, int Id, double CpuPercent, double RamMb, double IoKbPerSec);

public sealed class ResourceSampler
{
    private readonly DiskMonitor _diskMonitor;
    private readonly Dictionary<int, TimeSpan> _lastCpuTimes = new();
    private readonly Dictionary<int, ulong> _lastIoBytes = new();
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

    // Uma única enumeração de todo processo por tick, reaproveitada pra duas coisas: somar
    // CPU/RAM de quem bate os padrões de exclusão (pro cálculo de uso "líquido") e manter
    // _lastAllProcessUsages atualizado (pra servir GetTopProcesses sem nova varredura). Antes,
    // cada uma dessas coisas tinha seu próprio código — a exclusão aqui, o top-processos com
    // varredura dedicada de 500ms em ProcessSnapshotter.CaptureAsync.
    private (double CpuSeconds, long RamBytes) WalkProcesses(IReadOnlyList<string> excludedPatterns, double? elapsedSeconds)
    {
        double excludedCpuSeconds = 0;
        long excludedRamBytes = 0;
        var currentPids = new HashSet<int>();
        var usages = new List<ProcessUsage>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                currentPids.Add(process.Id);

                var cpuTime = process.TotalProcessorTime;
                var ramBytes = process.WorkingSet64;
                var isExcluded = excludedPatterns.Count > 0 && MatchesAnyPattern(process.ProcessName, excludedPatterns);

                double cpuPercent = 0;
                double ioKbPerSec = 0;

                if (elapsedSeconds is { } seconds && seconds > 0)
                {
                    if (_lastCpuTimes.TryGetValue(process.Id, out var previousCpuTime))
                    {
                        var cpuDeltaSeconds = Math.Max(0, (cpuTime - previousCpuTime).TotalSeconds);
                        cpuPercent = Math.Clamp(cpuDeltaSeconds / (seconds * Environment.ProcessorCount) * 100.0, 0, 100);
                        if (isExcluded)
                        {
                            excludedCpuSeconds += cpuDeltaSeconds;
                        }
                    }

                    // Leitura de I/O por processo não é exposta pelo Process do .NET — via
                    // P/Invoke de GetProcessIoCounters (kernel32), mesmo padrão de delta da CPU.
                    if (GetProcessIoCounters(process.Handle, out var counters))
                    {
                        var ioBytes = counters.ReadTransferCount + counters.WriteTransferCount;
                        if (_lastIoBytes.TryGetValue(process.Id, out var previousIoBytes))
                        {
                            var ioBytesDelta = Math.Max(0, (long)(ioBytes - previousIoBytes));
                            ioKbPerSec = ioBytesDelta / seconds / 1024.0;
                        }

                        _lastIoBytes[process.Id] = ioBytes;
                    }
                }

                _lastCpuTimes[process.Id] = cpuTime;

                if (isExcluded)
                {
                    excludedRamBytes += ramBytes;
                }

                usages.Add(new ProcessUsage(process.ProcessName, process.Id, cpuPercent, ramBytes / 1024.0 / 1024.0, ioKbPerSec));
            }
            catch (Exception)
            {
                // processo pode ter terminado ou negado acesso durante a leitura; ignora.
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var pid in _lastCpuTimes.Keys.Except(currentPids).ToList())
        {
            _lastCpuTimes.Remove(pid);
        }

        foreach (var pid in _lastIoBytes.Keys.Except(currentPids).ToList())
        {
            _lastIoBytes.Remove(pid);
        }

        _lastAllProcessUsages = usages;

        return (excludedCpuSeconds, excludedRamBytes);
    }

    private static bool MatchesAnyPattern(string processName, IReadOnlyList<string> patterns)
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

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);
}
