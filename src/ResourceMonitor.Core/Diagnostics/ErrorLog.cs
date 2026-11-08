using System.Globalization;

namespace ResourceMonitor.Diagnostics;

// Log de avisos/erros que vale em qualquer build (diferente do trace de depuração, só Debug +
// --trace). Um arquivo só, errors.log na pasta de logs, girando pra errors.log.1 ao passar de
// MaxBytes — no máximo ~2 MB no disco. Sem Initialize, ou se o disco recusar, não faz nada:
// registrar um erro nunca pode virar um erro novo.
public static class ErrorLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Lock = new();
    private static string? _filePath;

    public static string? FilePath => _filePath;

    public static void Initialize(string logDirectory)
    {
        _filePath = Path.Combine(logDirectory, "errors.log");
    }

    public static void Write(string category, string message, Exception? exception = null)
    {
        var filePath = _filePath;
        if (filePath is null)
        {
            return;
        }

        var line = $"[{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}] [{category}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                var file = new FileInfo(filePath);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(filePath, filePath + ".1", overwrite: true);
                }

                File.AppendAllText(filePath, line + Environment.NewLine);
            }
            catch (Exception)
            {
            }
        }
    }
}
