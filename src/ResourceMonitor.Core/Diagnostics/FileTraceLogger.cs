using System.Globalization;

namespace ResourceMonitor.Diagnostics;

// Log de trace pra depuração manual (não é o log de operação normal do app).
// Só é instanciado pelo TraceLoggerFactory quando faz sentido (build Debug + --trace).
public sealed class FileTraceLogger : ITraceLogger
{
    private readonly object _lock = new();
    private readonly string _logFilePath;

    public FileTraceLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory, "trace.log");
    }

    public bool IsEnabled => true;

    public void Trace(string category, string message)
    {
        var line = $"[{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}] [{category}] {message}";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // trace não pode derrubar o app.
            }
        }
    }
}
