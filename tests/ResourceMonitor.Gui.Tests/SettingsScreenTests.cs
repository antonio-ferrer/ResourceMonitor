using Microsoft.Data.Sqlite;
using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Core.Tests;
using ResourceMonitor.Gui.Services;
using ResourceMonitor.Gui.ViewModels;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Gui.Tests;

// Tela de Configurações (MonitoringViewModel) montada como o App monta, mas numa pasta
// temporária e com diálogos/registro falsos. O monitoramento nunca é iniciado aqui.
public sealed class SettingsScreenTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly MonitoringService _service = new();
    private readonly FakeDialogs _dialogs = new();

    private string SettingsFile => _folder.File("appsettings.json");

    private (MonitoringViewModel ViewModel, SettingsSession Settings) Open(FakeAutoStart? autoStart = null)
    {
        var settings = new SettingsSession(_folder.Path, SettingsFile);
        var controller = new MonitoringController(_service, settings, _folder.File("state.json"));
        var viewModel = new MonitoringViewModel(
            controller, settings, new FakeTrayNotifier(), _dialogs, autoStart ?? new FakeAutoStart(false));
        return (viewModel, settings);
    }

    [Fact]
    public void Sem_mudanca_nao_grava_o_arquivo()
    {
        var (viewModel, _) = Open();
        // Arquivo recém-criado não tem os limites por disco (a tela monta a lista a partir dos
        // discos reais), então a primeira gravação sempre acontece. Daí em diante, só com mudança.
        viewModel.SaveIfChanged();
        File.Delete(SettingsFile);
        viewModel.SavedNoticeText = string.Empty;

        viewModel.SaveIfChanged();

        Assert.False(File.Exists(SettingsFile));
        Assert.Equal(string.Empty, viewModel.SavedNoticeText);
    }

    [Fact]
    public void Com_mudanca_grava_ajustado_e_avisa()
    {
        var (viewModel, settings) = Open();

        viewModel.SampleIntervalSeconds = 0;
        viewModel.SaveIfChanged();

        Assert.Equal(1, AppSettingsStore.Load(SettingsFile).SampleIntervalSeconds);
        Assert.Equal(1, settings.Current.SampleIntervalSeconds);
        Assert.Equal(1, viewModel.SampleIntervalSeconds); // valor ajustado volta pro campo
        Assert.StartsWith("Configurações salvas", viewModel.SavedNoticeText);
    }

    [Fact]
    public void Gravar_preserva_a_pasta_de_dados_que_nao_tem_campo_na_tela()
    {
        AppSettingsStore.Save(new MonitorSettings { LogDirectory = "dados" }, SettingsFile);
        var (viewModel, settings) = Open();

        viewModel.PeakRetentionDays = 30;
        viewModel.SaveIfChanged();

        var saved = AppSettingsStore.Load(SettingsFile);
        Assert.Equal("dados", saved.LogDirectory);
        Assert.Equal(30, saved.PeakRetentionDays);
        Assert.Equal(Path.Combine(_folder.Path, "dados", "resourcemonitor.db"), settings.DatabasePath);
    }

    // Abrir com "Iniciar com o Windows" ligado regrava a entrada (mantém o caminho do exe em
    // dia); desligado não toca no registro.
    [Theory]
    [InlineData(true, new[] { true })]
    [InlineData(false, new bool[0])]
    public void Abrir_a_tela_so_regrava_o_registro_se_ja_estava_ligado(bool enabled, bool[] expectedCalls)
    {
        var autoStart = new FakeAutoStart(enabled);

        Open(autoStart);

        Assert.Equal(expectedCalls, autoStart.SetCalls);
    }

    [Fact]
    public void Marcar_e_desmarcar_iniciar_com_o_windows_grava_no_registro()
    {
        var autoStart = new FakeAutoStart(false);
        var (viewModel, _) = Open(autoStart);

        viewModel.StartWithWindows = true;
        viewModel.StartWithWindows = false;

        Assert.Equal(new[] { true, false }, autoStart.SetCalls);
    }

    private static void Seed(string databasePath)
    {
        using var database = new PermanentDatabase(databasePath);
        database.InsertAlertEvent(new AlertEvent(DateTimeOffset.UtcNow, AlertEventType.Start, "CPU", 95, 95, 90, null));
        database.UpsertDailyAggregate(DateOnly.FromDateTime(DateTime.Today), 10, 40, 1, 60, "C:\\");
    }

    private static long Count(string databasePath, string table)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void Limpeza_respeita_o_nao()
    {
        var (viewModel, settings) = Open();
        Seed(settings.DatabasePath);
        _dialogs.ConfirmAnswer = false;

        viewModel.ClearSelectedCommand.Execute(null);

        Assert.Single(_dialogs.Confirmations);
        Assert.Equal(1, Count(settings.DatabasePath, "AlertEvents"));
        Assert.Equal(1, Count(settings.DatabasePath, "DailyAggregates"));
    }

    [Fact]
    public void Limpeza_com_sim_apaga_so_o_que_estava_marcado()
    {
        var (viewModel, settings) = Open();
        Seed(settings.DatabasePath);
        _dialogs.ConfirmAnswer = true;
        viewModel.ClearPeaksSelected = false;

        viewModel.ClearSelectedCommand.Execute(null);

        Assert.Equal(1, Count(settings.DatabasePath, "AlertEvents"));
        Assert.Equal(0, Count(settings.DatabasePath, "DailyAggregates"));
        Assert.Equal("Limpeza concluída.", viewModel.StatusText);
    }

    [Fact]
    public void Limpeza_sem_nada_marcado_nao_pergunta()
    {
        var (viewModel, _) = Open();
        viewModel.ClearPeaksSelected = false;
        viewModel.ClearTrendSelected = false;

        viewModel.ClearSelectedCommand.Execute(null);

        Assert.Empty(_dialogs.Confirmations);
        Assert.Empty(_dialogs.Warnings);
    }

    public void Dispose()
    {
        _service.Dispose();
        _folder.Dispose();
    }
}
