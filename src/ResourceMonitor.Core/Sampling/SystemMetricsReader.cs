using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ResourceMonitor.Sampling;

internal static class SystemMetricsReader
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    public readonly record struct CpuTimesSnapshot(long IdleTicks, long KernelTicks, long UserTicks);

    public static CpuTimesSnapshot ReadCpuTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new InvalidOperationException("GetSystemTimes falhou.");
        }

        return new CpuTimesSnapshot(ToTicks(idle), ToTicks(kernel), ToTicks(user));
    }

    private static long ToTicks(FILETIME ft) => ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    public readonly record struct MemoryInfo(double PercentUsed, ulong TotalPhysBytes, ulong AvailPhysBytes);

    public static MemoryInfo ReadMemoryInfo()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("GlobalMemoryStatusEx falhou.");
        }

        return new MemoryInfo(status.dwMemoryLoad, status.ullTotalPhys, status.ullAvailPhys);
    }
}
