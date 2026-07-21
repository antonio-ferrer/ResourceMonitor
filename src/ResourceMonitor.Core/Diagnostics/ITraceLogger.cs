namespace ResourceMonitor.Diagnostics;

public interface ITraceLogger
{
    bool IsEnabled { get; }

    void Trace(string category, string message);
}
