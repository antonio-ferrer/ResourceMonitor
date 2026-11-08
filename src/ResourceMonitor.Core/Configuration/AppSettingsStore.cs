using System.Text.Json;
using ResourceMonitor.Diagnostics;

namespace ResourceMonitor.Configuration;

// Config e banco moram em %LOCALAPPDATA%\ResourceMonitor — console e GUI compartilham
// o mesmo estado, independente de qual .exe foi aberto.
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string GetDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceMonitor");

    public static string GetSettingsFilePath() => Path.Combine(GetDataDirectory(), "appsettings.json");

    public static MonitorSettings Load() => Load(GetSettingsFilePath());

    public static void Save(MonitorSettings settings) => Save(settings, GetSettingsFilePath());

    // Arquivo ilegível (JSON quebrado, editado à mão errado) não impede o app de abrir: fica
    // guardado como .invalid pra consulta, o motivo vai pro errors.log, e seguem os padrões.
    internal static MonitorSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new MonitorSettings();
            Save(defaults, path);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<MonitorSettings>(json, SerializerOptions) ?? new MonitorSettings();
            return Normalize(settings);
        }
        catch (JsonException ex)
        {
            ErrorLog.Write("Configurações", $"appsettings.json inválido, usando os padrões (original guardado como .invalid).", ex);
            File.Move(path, path + ".invalid", overwrite: true);
            var defaults = new MonitorSettings();
            Save(defaults, path);
            return defaults;
        }
    }

    // Grava num .tmp e troca pelo arquivo real de uma vez: uma queda no meio da gravação deixa
    // no máximo um .tmp sobrando, nunca um appsettings.json pela metade.
    internal static void Save(MonitorSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    // Valores impossíveis vindos do arquivo (editado à mão, versão antiga) viram o mínimo
    // aceitável — um intervalo 0, por exemplo, derrubava o loop de monitoramento na largada.
    // Público: a GUI aplica o mesmo ajuste antes de gravar o que foi digitado.
    public static MonitorSettings Normalize(MonitorSettings settings)
    {
        settings.SampleIntervalSeconds = Math.Max(1, settings.SampleIntervalSeconds);
        settings.ConsecutiveBreachesToAlert = Math.Max(1, settings.ConsecutiveBreachesToAlert);
        settings.ConsecutiveRecoveriesToClear = Math.Max(1, settings.ConsecutiveRecoveriesToClear);
        settings.TopProcessCount = Math.Max(1, settings.TopProcessCount);
        settings.PreEventSeconds = Math.Max(0, settings.PreEventSeconds);
        settings.PostEventSeconds = Math.Max(0, settings.PostEventSeconds);
        if (string.IsNullOrWhiteSpace(settings.LogDirectory))
        {
            settings.LogDirectory = new MonitorSettings().LogDirectory;
        }

        settings.ExcludedProcesses ??= new MonitorSettings().ExcludedProcesses;
        settings.Thresholds ??= new ThresholdSettings();
        settings.Thresholds.DiskFreeThresholds ??= new List<DiskThreshold>();
        return settings;
    }
}
