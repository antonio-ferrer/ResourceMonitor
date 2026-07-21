using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ResourceMonitor.Sampling;

public sealed class ResourceSampler
{
    private readonly DiskMonitor _diskMonitor;
    private readonly Dictionary<int, TimeSpan> _lastExcludedCpuTimes = new();

    private SystemMetricsReader.CpuTimesSnapshot? _lastCpuTimes;
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

        ResourceSample? result = null;

        if (_lastCpuTimes is { } previousCpuTimes && _lastSampleTime is { } previousSampleTime)
        {
            var elapsedSeconds = (now - previousSampleTime).TotalSeconds;
            if (elapsedSeconds > 0)
            {
                var idleDelta = cpuTimes.IdleTicks - previousCpuTimes.IdleTicks;
                var kernelDelta = cpuTimes.KernelTicks - previousCpuTimes.KernelTicks;
                var userDelta = cpuTimes.UserTicks - previousCpuTimes.UserTicks;
                var totalDelta = kernelDelta + userDelta;

                var cpuRawPercent = totalDelta > 0
                    ? Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0, 100)
                    : 0;

                var (excludedCpuSeconds, excludedRamBytes) = SampleExcludedProcesses(excludedProcessPatterns);

                var excludedCpuPercent = Math.Clamp(
                    excludedCpuSeconds / (elapsedSeconds * Environment.ProcessorCount) * 100.0, 0, 100);

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
        }

        _lastCpuTimes = cpuTimes;
        _lastSampleTime = now;

        return result;
    }

    // Soma CPU (delta de TotalProcessorTime desde o último tick) e RAM (WorkingSet64 atual)
    // de todo processo cujo nome bate com algum padrão da lista de exclusão.
    private (double CpuSeconds, long RamBytes) SampleExcludedProcesses(IReadOnlyList<string> patterns)
    {
        double cpuSeconds = 0;
        long ramBytes = 0;
        var currentPids = new HashSet<int>();

        if (patterns.Count == 0)
        {
            _lastExcludedCpuTimes.Clear();
            return (0, 0);
        }

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!MatchesAnyPattern(process.ProcessName, patterns))
                {
                    continue;
                }

                currentPids.Add(process.Id);

                var cpuTime = process.TotalProcessorTime;
                if (_lastExcludedCpuTimes.TryGetValue(process.Id, out var previous))
                {
                    cpuSeconds += Math.Max(0, (cpuTime - previous).TotalSeconds);
                }

                _lastExcludedCpuTimes[process.Id] = cpuTime;
                ramBytes += process.WorkingSet64;
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

        foreach (var pid in _lastExcludedCpuTimes.Keys.Except(currentPids).ToList())
        {
            _lastExcludedCpuTimes.Remove(pid);
        }

        return (cpuSeconds, ramBytes);
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
}
