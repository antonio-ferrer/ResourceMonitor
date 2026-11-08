using ResourceMonitor.Configuration;
using ResourceMonitor.Core.Tests;
using ResourceMonitor.Gui.Services;
using ResourceMonitor.Monitoring;

namespace ResourceMonitor.Gui.Tests;

public sealed class MonitoringControllerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly MonitoringService _service = new();
    private readonly MonitoringController _controller;

    private string StateFile => _folder.File("state.json");

    public MonitoringControllerTests()
    {
        var settings = new SettingsSession(_folder.Path, _folder.File("appsettings.json"));
        _controller = new MonitoringController(_service, settings, StateFile);
    }

    [Fact]
    public async Task Iniciar_e_parar_pelo_usuario_lembram_a_escolha()
    {
        _controller.StartByUser();
        Assert.True(_controller.IsRunning);
        Assert.True(MonitoringStateStore.LoadMonitoringActive(StateFile));

        await _controller.StopByUserAsync();
        Assert.False(_controller.IsRunning);
        Assert.False(MonitoringStateStore.LoadMonitoringActive(StateFile));
    }

    [Fact]
    public void Abrir_depois_de_parado_pelo_usuario_continua_parado()
    {
        MonitoringStateStore.SaveMonitoringActive(false, StateFile);

        _controller.StartIfLeftRunning();

        Assert.False(_controller.IsRunning);
    }

    public void Dispose()
    {
        _service.Dispose();
        _folder.Dispose();
    }
}
