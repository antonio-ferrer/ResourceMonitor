using ResourceMonitor.Configuration;

namespace ResourceMonitor.Storage;

// Guarda capturas pendentes entre o instante em que um alerta dispara (pico conhecido)
// e o momento em que a janela pós-pico termina de acontecer (amostras vêm do RecentSamplesBuffer).
public sealed class EventCaptureCoordinator
{
    private readonly MonitorSettings _settings;
    private readonly List<PendingCapture> _pending = new();

    public EventCaptureCoordinator(MonitorSettings settings)
    {
        _settings = settings;
    }

    public void BeginCapture(long alertEventId, DateTimeOffset peakTimestamp)
    {
        var pre = TimeSpan.FromSeconds(_settings.PreEventSeconds);
        var post = TimeSpan.FromSeconds(_settings.PostEventSeconds);

        _pending.Add(new PendingCapture(alertEventId, peakTimestamp - pre, peakTimestamp + post));
    }

    // Copia pro banco permanente qualquer captura cuja janela pós-pico já tenha se completado.
    public void FlushReady(DateTimeOffset now, RecentSamplesBuffer recentSamples, PermanentDatabase permanent)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var capture = _pending[i];
            if (now < capture.WindowEnd)
            {
                continue;
            }

            Capture(capture, recentSamples, permanent);
            _pending.RemoveAt(i);
        }
    }

    // Usado no encerramento do app: grava o que estiver disponível no buffer pras capturas
    // ainda pendentes, em vez de perdê-las.
    public void FlushAll(RecentSamplesBuffer recentSamples, PermanentDatabase permanent)
    {
        foreach (var capture in _pending)
        {
            Capture(capture, recentSamples, permanent);
        }

        _pending.Clear();
    }

    private static void Capture(PendingCapture capture, RecentSamplesBuffer recentSamples, PermanentDatabase permanent)
    {
        var samples = recentSamples.GetRange(capture.WindowStart, capture.WindowEnd);
        if (samples.Count > 0)
        {
            permanent.InsertSampleWindow(capture.AlertEventId, samples);
        }
    }

    private readonly record struct PendingCapture(long AlertEventId, DateTimeOffset WindowStart, DateTimeOffset WindowEnd);
}
