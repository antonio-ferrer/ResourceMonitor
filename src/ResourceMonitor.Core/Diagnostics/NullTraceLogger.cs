namespace ResourceMonitor.Diagnostics;

// Usado sempre que o trace não está ativo (build Release, ou build Debug sem --trace) —
// custo zero, sem I/O.
public sealed class NullTraceLogger : ITraceLogger
{
    public bool IsEnabled => false;

    public void Trace(string category, string message)
    {
    }
}
