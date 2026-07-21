namespace ResourceMonitor.Sampling;

public sealed record ResourceSample(
    DateTimeOffset Timestamp,
    double CpuRawPercent,
    double CpuAdjustedPercent,
    double RamRawPercent,
    double RamAdjustedPercent,
    double RamTotalGb,
    double RamAvailableGb,
    IReadOnlyList<DiskSample> Disks);

public sealed record DiskSample(
    string DriveName,
    double FreePercent,
    double FreeGb,
    double TotalGb,
    double IoPercent);
