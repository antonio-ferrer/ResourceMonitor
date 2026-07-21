using System.Diagnostics;

namespace ResourceMonitor.Sampling;

public sealed class ResourceSampler
{
    private readonly Process _selfProcess = Process.GetCurrentProcess();
    private readonly DiskMonitor _diskMonitor;

    private SystemMetricsReader.CpuTimesSnapshot? _lastCpuTimes;
    private TimeSpan? _lastSelfCpuTime;
    private DateTimeOffset? _lastSampleTime;

    public ResourceSampler(DiskMonitor diskMonitor)
    {
        _diskMonitor = diskMonitor;
    }

    // Retorna null na primeira chamada (amostra de aquecimento, ainda sem delta pra calcular).
    public ResourceSample? Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var cpuTimes = SystemMetricsReader.ReadCpuTimes();
        _selfProcess.Refresh();
        var selfCpuTime = _selfProcess.TotalProcessorTime;

        ResourceSample? result = null;

        if (_lastCpuTimes is { } previousCpuTimes &&
            _lastSelfCpuTime is { } previousSelfCpuTime &&
            _lastSampleTime is { } previousSampleTime)
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

                var selfCpuSeconds = (selfCpuTime - previousSelfCpuTime).TotalSeconds;
                var selfCpuPercent = Math.Clamp(
                    selfCpuSeconds / (elapsedSeconds * Environment.ProcessorCount) * 100.0, 0, 100);

                var cpuAdjustedPercent = Math.Max(0, cpuRawPercent - selfCpuPercent);

                var memoryInfo = SystemMetricsReader.ReadMemoryInfo();
                var selfRamBytes = _selfProcess.WorkingSet64;
                var selfRamPercent = memoryInfo.TotalPhysBytes > 0
                    ? (double)selfRamBytes / memoryInfo.TotalPhysBytes * 100.0
                    : 0;
                var ramAdjustedPercent = Math.Max(0, memoryInfo.PercentUsed - selfRamPercent);

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
        _lastSelfCpuTime = selfCpuTime;
        _lastSampleTime = now;

        return result;
    }
}
