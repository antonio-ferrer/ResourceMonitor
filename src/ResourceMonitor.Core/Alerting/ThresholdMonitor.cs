using ResourceMonitor.Configuration;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Alerting;

public sealed class ThresholdMonitor
{
    private readonly MonitorSettings _settings;
    private readonly Dictionary<string, MetricState> _states = new();

    // Espaço livre em disco não usa o pipeline de episódio (ver EvaluateDiskFreeSpace) — tem
    // sua própria histerese, mais simples, sem peak-tracking.
    private readonly Dictionary<string, DiskFreeState> _diskStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabledDrives = new(StringComparer.OrdinalIgnoreCase);

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
            EvaluateMetric(events, "DiscoIO", disk.DriveName, disk.IoPercent, null,
                _settings.Thresholds.DiskIoPercent, isAboveThreshold: true, sample.Timestamp);
        }

        return events;
    }

    // Espaço em disco é "especialista": threshold por unidade, notificação pontual (não gera
    // AlertEvent/episódio). Itera pelos discos CONFIGURADOS (não pelos presentes na amostra) pra
    // conseguir detectar quando um disco configurado sumiu e desabilitá-lo pro resto da execução.
    public IReadOnlyList<DiskSpaceWarning> EvaluateDiskFreeSpace(ResourceSample sample)
    {
        var warnings = new List<DiskSpaceWarning>();
        var disksByName = sample.Disks.ToDictionary(d => d.DriveName, StringComparer.OrdinalIgnoreCase);

        foreach (var threshold in _settings.Thresholds.DiskFreeThresholds)
        {
            var driveName = threshold.DriveName;
            if (_disabledDrives.Contains(driveName))
            {
                continue; // já ficou indisponível antes nessa execução — só volta num próximo Start
            }

            if (!disksByName.TryGetValue(driveName, out var disk))
            {
                _disabledDrives.Add(driveName); // sumiu da lista de discos fixos — desabilita pro resto da execução
                continue;
            }

            if (!_diskStates.TryGetValue(driveName, out var state))
            {
                state = new DiskFreeState();
                _diskStates[driveName] = state;
            }

            var isBreaching = disk.FreePercent < threshold.MinFreePercent;
            if (isBreaching)
            {
                state.ConsecutiveBreaches++;
                state.ConsecutiveRecoveries = 0;
                if (!state.IsActive && state.ConsecutiveBreaches >= _settings.ConsecutiveBreachesToAlert)
                {
                    state.IsActive = true;
                    warnings.Add(new DiskSpaceWarning(driveName, disk.FreePercent, threshold.MinFreePercent, sample.Timestamp));
                }
            }
            else
            {
                state.ConsecutiveRecoveries++;
                state.ConsecutiveBreaches = 0;
                if (state.IsActive && state.ConsecutiveRecoveries >= _settings.ConsecutiveRecoveriesToClear)
                {
                    state.IsActive = false; // permite notificar de novo numa próxima queda
                }
            }
        }

        return warnings;
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

    private sealed class DiskFreeState
    {
        public bool IsActive;
        public int ConsecutiveBreaches;
        public int ConsecutiveRecoveries;
    }
}
