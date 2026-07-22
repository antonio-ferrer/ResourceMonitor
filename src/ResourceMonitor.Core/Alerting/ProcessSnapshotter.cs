using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResourceMonitor.Alerting;

public sealed record ProcessUsage(string Name, int Id, double CpuPercent, double RamMb, double IoKbPerSec);

public static class ProcessSnapshotter
{
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMilliseconds(500);

    public static async Task<(IReadOnlyList<ProcessUsage> TopByCpu, IReadOnlyList<ProcessUsage> TopByRam, IReadOnlyList<ProcessUsage> TopByIo)> CaptureAsync(int topN)
    {
        var processes = Process.GetProcesses();
        var initialTimes = new Dictionary<int, TimeSpan>();
        var initialIoBytes = new Dictionary<int, ulong>();

        foreach (var process in processes)
        {
            try
            {
                initialTimes[process.Id] = process.TotalProcessorTime;
                if (GetProcessIoCounters(process.Handle, out var counters))
                {
                    initialIoBytes[process.Id] = counters.ReadTransferCount + counters.WriteTransferCount;
                }
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

                // Leitura de I/O por processo não é exposta pelo Process do .NET — via P/Invoke
                // de GetProcessIoCounters (kernel32), mesmo padrão de delta usado pra CPU.
                var ioKbPerSec = 0.0;
                if (initialIoBytes.TryGetValue(process.Id, out var initialIo) && GetProcessIoCounters(process.Handle, out var finalCounters))
                {
                    var ioBytesDelta = (long)(finalCounters.ReadTransferCount + finalCounters.WriteTransferCount) - (long)initialIo;
                    ioKbPerSec = Math.Max(0, ioBytesDelta) / SampleWindow.TotalSeconds / 1024.0;
                }

                usages.Add(new ProcessUsage(process.ProcessName, process.Id, cpuPercent, ramMb, ioKbPerSec));
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
        var topByIo = usages.OrderByDescending(u => u.IoKbPerSec).Take(topN).ToList();

        return (topByCpu, topByRam, topByIo);
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
