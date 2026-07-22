namespace ResourceMonitor.Alerting;

// Aviso pontual de espaço em disco baixo — diferente de AlertEvent, não vira um episódio
// Start/End persistido (ver ThresholdMonitor.EvaluateDiskFreeSpace).
public sealed record DiskSpaceWarning(
    string DriveName,
    double FreePercent,
    double MinFreePercent,
    DateTimeOffset Timestamp);
