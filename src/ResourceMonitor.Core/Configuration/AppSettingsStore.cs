using System.Text.Json;

namespace ResourceMonitor.Configuration;

// Config e banco moram em %LOCALAPPDATA%\ResourceMonitor — console e GUI compartilham
// o mesmo estado, independente de qual .exe foi aberto.
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string GetDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceMonitor");

    public static string GetSettingsFilePath() => Path.Combine(GetDataDirectory(), "appsettings.json");

    public static MonitorSettings Load()
    {
        var path = GetSettingsFilePath();
        if (!File.Exists(path))
        {
            var defaults = new MonitorSettings();
            Save(defaults);
            return defaults;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MonitorSettings>(json, SerializerOptions) ?? new MonitorSettings();
    }

    public static void Save(MonitorSettings settings)
    {
        Directory.CreateDirectory(GetDataDirectory());
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        File.WriteAllText(GetSettingsFilePath(), json);
    }
}
