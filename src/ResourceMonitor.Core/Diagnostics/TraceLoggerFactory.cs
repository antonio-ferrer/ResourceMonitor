namespace ResourceMonitor.Diagnostics;

// Único ponto de decisão de "quando o trace liga": build Debug (o bloco #if nem compila
// em Release) e só se --trace foi passado na linha de comando.
public static class TraceLoggerFactory
{
    public static ITraceLogger Create(IReadOnlyList<string> args, string logDirectory)
    {
#if DEBUG
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--trace", StringComparison.OrdinalIgnoreCase))
            {
                return new FileTraceLogger(logDirectory);
            }
        }
#endif
        return new NullTraceLogger();
    }
}
