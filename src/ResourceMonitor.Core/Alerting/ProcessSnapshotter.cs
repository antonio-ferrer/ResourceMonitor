using System.Diagnostics;

namespace ResourceMonitor.Alerting;

public sealed record ProcessUsage(string Name, int Id, double CpuPercent, double RamMb);

public static class ProcessSnapshotter
{
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(500);

    public static async Task<(IReadOnlyList<ProcessUsage> TopByCpu, IReadOnlyList<ProcessUsage> TopByRam)> CaptureAsync(int topN)
    {
        var processes = Process.GetProcesses();
        var initialTimes = new Dictionary<int, TimeSpan>();

        foreach (var process in processes)
        {
            try
            {
                initialTimes[process.Id] = process.TotalProcessorTime;
            }
            catch (Exception)
            {
                // Processos de sistema sem permissão de leitura, ou que terminaram: ignora.
            }
        }

        await Task.Delay(SampleWindow);

        var usages = new List<ProcessUsage>();
        var coreCount = Environment.ProcessorCount;

        foreach (var process in processes)
        {
            try
            {
                process.Refresh();
                if (!initialTimes.TryGetValue(process.Id, out var initialTime))
                {
                    continue;
                }

                var cpuDelta = (process.TotalProcessorTime - initialTime).TotalSeconds;
                var cpuPercent = Math.Clamp(cpuDelta / (SampleWindow.TotalSeconds * coreCount) * 100.0, 0, 100);
                var ramMb = process.WorkingSet64 / 1024.0 / 1024.0;

                usages.Add(new ProcessUsage(process.ProcessName, process.Id, cpuPercent, ramMb));
            }
            catch (Exception)
            {
                // Idem: processo pode ter terminado durante a coleta.
            }
            finally
            {
                process.Dispose();
            }
        }

        var topByCpu = usages.OrderByDescending(u => u.CpuPercent).Take(topN).ToList();
        var topByRam = usages.OrderByDescending(u => u.RamMb).Take(topN).ToList();

        return (topByCpu, topByRam);
    }
}
