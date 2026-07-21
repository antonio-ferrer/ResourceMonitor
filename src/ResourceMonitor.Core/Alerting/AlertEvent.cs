namespace ResourceMonitor.Alerting;

public enum AlertEventType
{
    Start,
    End
}

public sealed record AlertEvent(
    DateTimeOffset Timestamp,
    AlertEventType EventType,
    string Metric,
    double RawValue,
    double? AdjustedValue,
    double Threshold,
    string? DriveName,
    DateTimeOffset? PeakTimestamp = null);
