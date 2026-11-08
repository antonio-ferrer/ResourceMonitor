using ResourceMonitor.Core.Tests;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Gui.ViewModels;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Gui.Tests;

public sealed class TemplatesScreenTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly string _databasePath;

    public TemplatesScreenTests() => _databasePath = _folder.CreateDatabase();

    private TemplatesViewModel Open() =>
        new(() => _databasePath, new TemplateQueries(new NullTraceLogger()), _dialogs);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Excluir_template_respeita_a_resposta(bool answer, bool shouldRemain)
    {
        PermanentDatabase.InsertTemplate(_databasePath, "Meu template", "SELECT 1");
        var viewModel = Open();
        viewModel.SelectedTemplate = viewModel.Templates.Single(t => t.Name == "Meu template");
        _dialogs.ConfirmAnswer = answer;

        viewModel.ExcluirTemplateCommand.Execute(null);

        Assert.Single(_dialogs.Confirmations);
        Assert.Equal(shouldRemain, viewModel.Templates.Any(t => t.Name == "Meu template"));
    }

    [Fact]
    public void Template_embutido_nao_pode_ser_excluido()
    {
        var viewModel = Open();
        viewModel.SelectedTemplate = viewModel.Templates.First(t => t.IsBuiltIn);

        Assert.False(viewModel.ExcluirTemplateCommand.CanExecute(null));
    }

    [Fact]
    public void Exportar_csv_cancelado_nao_cria_arquivo_e_escolhido_grava()
    {
        var viewModel = Open();
        viewModel.EditorText = "SELECT 'ok' AS resultado";
        viewModel.ExecutarCommand.Execute(null);
        var csvPath = _folder.File("saida.csv");

        _dialogs.SaveFileAnswer = null;
        viewModel.ExportarCsvCommand.Execute(null);
        Assert.False(File.Exists(csvPath));

        _dialogs.SaveFileAnswer = csvPath;
        viewModel.ExportarCsvCommand.Execute(null);
        Assert.Contains("resultado", File.ReadAllText(csvPath));
        Assert.Contains("ok", File.ReadAllText(csvPath));
    }

    public void Dispose() => _folder.Dispose();
}
