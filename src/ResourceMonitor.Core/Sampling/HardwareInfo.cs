using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ResourceMonitor.Sampling;

public sealed record HardwareDiskInfo(string DriveName, double TotalGb, double FreeGb, double FreePercent);

public sealed record HardwareInfo(
    string MachineName,
    string OperatingSystem,
    string ProcessorName,
    double RamTotalGb,
    IReadOnlyList<HardwareDiskInfo> Disks);

// Usado só no relatório impresso — não faz parte do loop de amostragem, por isso lê tudo
// sob demanda (via registro, não WMI, pra não precisar de um pacote NuGet novo).
public static class HardwareInfoReader
{
    public static HardwareInfo Capture()
    {
        var memoryInfo = SystemMetricsReader.ReadMemoryInfo();

        var disks = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => new HardwareDiskInfo(
                d.Name,
                d.TotalSize / 1024.0 / 1024.0 / 1024.0,
                d.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0,
                d.TotalSize > 0 ? (double)d.AvailableFreeSpace / d.TotalSize * 100.0 : 0))
            .ToList();

        return new HardwareInfo(
            Environment.MachineName,
            ReadOperatingSystemName(),
            ReadProcessorName(),
            memoryInfo.TotalPhysBytes / 1024.0 / 1024.0 / 1024.0,
            disks);
    }

    private static string ReadOperatingSystemName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var productName = key?.GetValue("ProductName") as string;
            if (productName is null)
            {
                return RuntimeInformation.OSDescription;
            }

            var displayVersion = key?.GetValue("DisplayVersion") as string;
            return displayVersion is null ? productName : $"{productName} {displayVersion}";
        }
        catch (Exception)
        {
            return RuntimeInformation.OSDescription;
        }
    }

    private static string ReadProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Desconhecido";
        }
        catch (Exception)
        {
            return "Desconhecido";
        }
    }
}
