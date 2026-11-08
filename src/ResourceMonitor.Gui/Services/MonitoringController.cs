using ResourceMonitor.Configuration;
using ResourceMonitor.Monitoring;

namespace ResourceMonitor.Gui.Services;

// Iniciar/Parar pedidos pelo usuário (tela de Configurações ou bandeja): além de agir, lembram a
// escolha no state.json pra próxima abertura. Parar porque o app está saindo não passa por aqui,
// então sair com o monitoramento ligado faz o app voltar monitorando.
public sealed class MonitoringController
{
    private readonly SettingsSession _settings;
    private readonly string _stateFilePath;

    public MonitoringController(MonitoringService service, SettingsSession settings, string stateFilePath)
    {
        Service = service;
        _settings = settings;
        _stateFilePath = stateFilePath;
    }

    public MonitoringService Service { get; }

    public bool IsRunning => Service.IsRunning;

    // Na abertura (e no boot com o Windows): volta como o usuário deixou.
    public void StartIfLeftRunning()
    {
        if (MonitoringStateStore.LoadMonitoringActive(_stateFilePath))
        {
            Service.Start(_settings.Current, _settings.DataDirectory);
        }
    }

    public void StartByUser()
    {
        MonitoringStateStore.SaveMonitoringActive(true, _stateFilePath);
        Service.Start(_settings.Current, _settings.DataDirectory);
    }

    public async Task StopByUserAsync()
    {
        MonitoringStateStore.SaveMonitoringActive(false, _stateFilePath);
        await Service.StopAsync();
    }
}
