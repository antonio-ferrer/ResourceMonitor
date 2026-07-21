using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Monitoring;

// Loop de monitoramento extraído do console pra ser reutilizado pela GUI (Start/Stop
// em vez de rodar do início ao fim de um processo). Roda em Task.Run — os eventos
// disparam numa thread de pool, quem assinar de uma UI precisa marshalar de volta.
public sealed class MonitoringService : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private CacheDatabase? _cache;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public event EventHandler<ResourceSample>? SampleCollected;
    public event EventHandler<AlertEvent>? AlertRaised;
    public event EventHandler<Exception>? Faulted;

    // "Dados correntes": tudo que está no cache volátil agora (a janela de retenção já se
    // auto-poda, então isso é só um limite superior generoso, não um filtro real).
    public IReadOnlyList<ResourceSample> GetCurrentSamples() =>
        (IReadOnlyList<ResourceSample>?)_cache?.GetSamplesInRange(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow)
            ?? Array.Empty<ResourceSample>();

    public void Start(MonitorSettings settings, string dataDirectory)
    {
        if (IsRunning)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => RunLoopAsync(settings, dataDirectory, token));
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loopTask is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _loopTask;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }

    private async Task RunLoopAsync(MonitorSettings settings, string dataDirectory, CancellationToken token)
    {
        var logDirectory = Path.Combine(dataDirectory, settings.LogDirectory);
        var databasePath = Path.Combine(logDirectory, "resourcemonitor.db");

        using var diskMonitor = new DiskMonitor();
        var sampler = new ResourceSampler(diskMonitor);
        var thresholdMonitor = new ThresholdMonitor(settings);

        using var cache = new CacheDatabase();
        _cache = cache;
        using var permanent = new PermanentDatabase(databasePath);
        var captureCoordinator = new EventCaptureCoordinator(settings);

        var cachePruneWindow = TimeSpan.FromSeconds(
            settings.PreEventSeconds + settings.PostEventSeconds + (2 * settings.SampleIntervalSeconds));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.SampleIntervalSeconds));

        // Alertas com Start já gravado mas ainda sem End — se o app for encerrado com algo
        // aberto aqui (Parar manual, crash), esses IDs são marcados como interrompidos no finally.
        var openAlerts = new Dictionary<string, long>();

        try
        {
            do
            {
                var sample = sampler.Sample(settings.ExcludedProcesses);
                if (sample is null)
                {
                    continue; // amostra de aquecimento
                }

                cache.InsertSample(sample);
                cache.Prune(sample.Timestamp - cachePruneWindow);

                SampleCollected?.Invoke(this, sample);

                var events = thresholdMonitor.Evaluate(sample);
                foreach (var alertEvent in events)
                {
                    var alertEventId = permanent.InsertAlertEvent(alertEvent);
                    var key = $"{alertEvent.Metric}|{alertEvent.DriveName}";

                    if (alertEvent.EventType == AlertEventType.Start)
                    {
                        openAlerts[key] = alertEventId;

                        var (topByCpu, topByRam) = await ProcessSnapshotter.CaptureAsync(settings.TopProcessCount);
                        permanent.InsertProcessSnapshots(alertEventId, "Cpu", topByCpu);
                        permanent.InsertProcessSnapshots(alertEventId, "Ram", topByRam);

                        captureCoordinator.BeginCapture(alertEventId, alertEvent.PeakTimestamp ?? alertEvent.Timestamp);
                    }
                    else
                    {
                        openAlerts.Remove(key);
                    }

                    AlertRaised?.Invoke(this, alertEvent);
                }

                // Heartbeat: enquanto o alerta segue ativo, atualiza o último instante confirmado.
                foreach (var openAlertId in openAlerts.Values)
                {
                    permanent.UpdateLastActive(openAlertId, sample.Timestamp);
                }

                captureCoordinator.FlushReady(sample.Timestamp, cache, permanent);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // parada solicitada via StopAsync
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            captureCoordinator.FlushAll(cache, permanent);

            foreach (var openAlertId in openAlerts.Values)
            {
                permanent.MarkInterrupted(openAlertId, DateTimeOffset.UtcNow);
            }

            _cache = null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
