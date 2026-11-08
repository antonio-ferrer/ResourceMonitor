using System.IO;
using System.Text.Json;
using ResourceMonitor.Configuration;

namespace ResourceMonitor.Gui.Services;

// Dona das configurações em vigor na GUI. Quem precisa do valor atual lê Current na hora (não
// guarda a referência): gravar troca o objeto inteiro.
public sealed class SettingsSession
{
    private readonly string _settingsFilePath;

    public SettingsSession(string dataDirectory, string settingsFilePath)
    {
        DataDirectory = dataDirectory;
        _settingsFilePath = settingsFilePath;
        Current = AppSettingsStore.Load(settingsFilePath);
    }

    public string DataDirectory { get; }

    public MonitorSettings Current { get; private set; }

    public string DatabasePath => Path.Combine(DataDirectory, Current.LogDirectory, "resourcemonitor.db");

    // Sem botão Salvar: a tela chama isso ao iniciar, trocar de tela, minimizar, fechar e sair.
    // Os valores passam pelo mesmo ajuste do carregamento (ex: intervalo 0 vira 1) e só gravam
    // se algo mudou em relação ao que está em vigor. Devolve se gravou.
    public bool SaveIfChanged(MonitorSettings edited)
    {
        var settings = AppSettingsStore.Normalize(edited);
        if (JsonSerializer.Serialize(settings) == JsonSerializer.Serialize(Current))
        {
            return false;
        }

        AppSettingsStore.Save(settings, _settingsFilePath);
        Current = settings;
        return true;
    }
}
