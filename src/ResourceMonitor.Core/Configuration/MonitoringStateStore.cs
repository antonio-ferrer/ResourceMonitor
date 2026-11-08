using System.Text.Json;
using ResourceMonitor.Diagnostics;

namespace ResourceMonitor.Configuration;

// Lembra se o usuário deixou o monitoramento ligado ou desligado na GUI, pra próxima abertura
// voltar do mesmo jeito. Fica fora do appsettings.json: o Salvar das Configurações regrava
// aquele arquivo inteiro e não deve mexer nesse estado.
public static class MonitoringStateStore
{
    private sealed class State
    {
        public bool MonitoringActive { get; set; } = true;
    }

    public static string GetFilePath() => Path.Combine(AppSettingsStore.GetDataDirectory(), "state.json");

    public static bool LoadMonitoringActive() => LoadMonitoringActive(GetFilePath());

    public static void SaveMonitoringActive(bool active) => SaveMonitoringActive(active, GetFilePath());

    // Sem arquivo (primeira execução) ou ilegível: monitorando — é pra isso que o app existe.
    internal static bool LoadMonitoringActive(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            return JsonSerializer.Deserialize<State>(File.ReadAllText(path))?.MonitoringActive ?? true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Estado", "state.json ilegível, considerando o monitoramento ligado.", ex);
            return true;
        }
    }

    // Mesma troca atômica do AppSettingsStore. Falha ao gravar não impede iniciar/parar —
    // só a escolha deixa de ser lembrada na próxima abertura.
    internal static void SaveMonitoringActive(bool active, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new State { MonitoringActive = active }));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Estado", "Não foi possível gravar o state.json.", ex);
        }
    }
}
