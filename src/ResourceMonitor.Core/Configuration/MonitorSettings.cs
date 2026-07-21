namespace ResourceMonitor.Configuration;

public sealed class MonitorSettings
{
    public int SampleIntervalSeconds { get; set; } = 5;
    public int ConsecutiveBreachesToAlert { get; set; } = 3;
    public int ConsecutiveRecoveriesToClear { get; set; } = 3;
    public int TopProcessCount { get; set; } = 5;
    public int PreEventSeconds { get; set; } = 60;
    public int PostEventSeconds { get; set; } = 60;
    public string LogDirectory { get; set; } = "logs";
    public List<string> ExcludedProcesses { get; set; } = new() { "ResourceMonitor*" };
    public ThresholdSettings Thresholds { get; set; } = new();
}

public sealed class ThresholdSettings
{
    public double CpuPercent { get; set; } = 90;
    public double RamPercent { get; set; } = 85;
    public double DiskFreePercentMin { get; set; } = 10;
    public double DiskIoPercent { get; set; } = 90;
}
