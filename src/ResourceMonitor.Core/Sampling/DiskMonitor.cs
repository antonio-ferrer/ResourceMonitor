using System.Diagnostics;

namespace ResourceMonitor.Sampling;

public sealed class DiskMonitor : IDisposable
{
    private readonly PerformanceCounter? _diskTimeCounter;

    public DiskMonitor()
    {
        try
        {
            _diskTimeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
            _diskTimeCounter.NextValue();
        }
        catch (Exception)
        {
            _diskTimeCounter = null;
        }
    }

    // "% Disk Time" só existe como contador agregado (_Total); o mesmo valor de I/O
    // é aplicado a todas as unidades porque o Windows não expõe I/O por letra de unidade.
    public IReadOnlyList<DiskSample> SampleDisks()
    {
        var ioPercent = 0.0;
        try
        {
            if (_diskTimeCounter is not null)
            {
                ioPercent = Math.Min(100, _diskTimeCounter.NextValue());
            }
        }
        catch (Exception)
        {
            ioPercent = 0;
        }

        var samples = new List<DiskSample>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            var totalGb = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;
            var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
            var freePercent = drive.TotalSize > 0
                ? (double)drive.AvailableFreeSpace / drive.TotalSize * 100.0
                : 0;

            samples.Add(new DiskSample(drive.Name, freePercent, freeGb, totalGb, ioPercent));
        }

        return samples;
    }

    public void Dispose()
    {
        _diskTimeCounter?.Dispose();
    }

    // Mesmo filtro de SampleDisks(), mas sem instanciar a classe (não toca no PerformanceCounter) —
    // usado pra popular a lista de discos na tela de Monitoramento antes de iniciar o monitoramento.
    public static IReadOnlyList<string> GetFixedDriveNames() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => d.Name)
            .ToList();
}
