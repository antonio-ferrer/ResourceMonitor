using ResourceMonitor.Configuration;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Alerting;

public sealed class ThresholdMonitor
{
    private readonly MonitorSettings _settings;
    private readonly Dictionary<string, MetricState> _states = new();

    public ThresholdMonitor(MonitorSettings settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<AlertEvent> Evaluate(ResourceSample sample)
    {
        var events = new List<AlertEvent>();

        EvaluateMetric(events, "CPU", null, sample.CpuRawPercent, sample.CpuAdjustedPercent,
            _settings.Thresholds.CpuPercent, isAboveThreshold: true, sample.Timestamp);

        EvaluateMetric(events, "RAM", null, sample.RamRawPercent, sample.RamAdjustedPercent,
            _settings.Thresholds.RamPercent, isAboveThreshold: true, sample.Timestamp);

        foreach (var disk in sample.Disks)
        {
            EvaluateMetric(events, "DiscoLivre", disk.DriveName, disk.FreePercent, null,
                _settings.Thresholds.DiskFreePercentMin, isAboveThreshold: false, sample.Timestamp);

            EvaluateMetric(events, "DiscoIO", disk.DriveName, disk.IoPercent, null,
                _settings.Thresholds.DiskIoPercent, isAboveThreshold: true, sample.Timestamp);
        }

        return events;
    }

    private void EvaluateMetric(
        List<AlertEvent> events,
        string metricName,
        string? driveName,
        double rawValue,
        double? adjustedValue,
        double threshold,
        bool isAboveThreshold,
        DateTimeOffset timestamp)
    {
        var key = driveName is null ? metricName : $"{metricName}:{driveName}";
        if (!_states.TryGetValue(key, out var state))
        {
            state = new MetricState();
            _states[key] = state;
        }

        // Para CPU/RAM, o valor ajustado (líquido, sem o próprio monitor) é o que decide o alerta.
        var evaluatedValue = adjustedValue ?? rawValue;
        var isBreaching = isAboveThreshold ? evaluatedValue > threshold : evaluatedValue < threshold;

        if (isBreaching)
        {
            state.ConsecutiveBreaches++;
            state.ConsecutiveRecoveries = 0;

            // Rastreia o valor mais extremo da sequência atual de violações — vira "o pico" se um Start disparar.
            var isMoreExtreme = state.PeakTimestamp is null ||
                (isAboveThreshold ? evaluatedValue > state.PeakValue : evaluatedValue < state.PeakValue);
            if (isMoreExtreme)
            {
                state.PeakValue = evaluatedValue;
                state.PeakTimestamp = timestamp;
            }

            if (!state.IsActive && state.ConsecutiveBreaches >= _settings.ConsecutiveBreachesToAlert)
            {
                state.IsActive = true;
                events.Add(new AlertEvent(
                    timestamp, AlertEventType.Start, metricName, rawValue, adjustedValue, threshold, driveName,
                    state.PeakTimestamp));
            }
        }
        else
        {
            state.ConsecutiveRecoveries++;
            state.ConsecutiveBreaches = 0;
            state.PeakTimestamp = null;

            if (state.IsActive && state.ConsecutiveRecoveries >= _settings.ConsecutiveRecoveriesToClear)
            {
                state.IsActive = false;
                events.Add(new AlertEvent(timestamp, AlertEventType.End, metricName, rawValue, adjustedValue, threshold, driveName));
            }
        }
    }

    private sealed class MetricState
    {
        public bool IsActive;
        public int ConsecutiveBreaches;
        public int ConsecutiveRecoveries;
        public double PeakValue;
        public DateTimeOffset? PeakTimestamp;
    }
}
