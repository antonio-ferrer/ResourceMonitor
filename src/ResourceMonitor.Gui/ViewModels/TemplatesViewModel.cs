using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Storage;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ResourceMonitor.Gui.ViewModels;

public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows);

// Dona da tela de Templates (ex-aba Dados). O texto do SQL em si vive no editor (WebView2,
// Assets/sql-editor.html) — este ViewModel não guarda uma cópia própria em memória, só lê
// (GetEditorTextAsync) e escreve (SetEditorText) via os dois delegates que o code-behind
// conecta, pull em vez de round-trip a cada tecla.
public partial class TemplatesViewModel : ObservableObject
{
    private readonly Func<string> _getDatabasePath;
    private readonly TemplateQueries _templateQueries;

    [ObservableProperty] private DateTime? periodFrom = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime? periodTo = DateTime.Today;
    [ObservableProperty] private TemplateRow? selectedTemplate;
    [ObservableProperty] private string statusText = string.Empty;

    [ObservableProperty] private bool isCreatingNew;
    [ObservableProperty] private string newTemplateName = string.Empty;
    [ObservableProperty] private string newTemplateError = string.Empty;

    // Editor SQL entra escondido por padrão (botão "Editor" faz o toggle) — o WebView2
    // continua existindo e com o texto carregado mesmo escondido, então Executar/Salvar
    // funcionam igual estando ele visível ou não.
    [ObservableProperty] private bool isEditorVisible;

    public ObservableCollection<TemplateRow> Templates { get; } = new();

    private QueryResult? _lastResult;

    public event EventHandler<QueryResult>? ResultReady;

    // Ligados pelo code-behind depois que o WebView2 do editor estiver pronto.
    public Func<Task<string>>? GetEditorTextAsync { get; set; }
    public Action<string>? SetEditorText { get; set; }

    public TemplatesViewModel(Func<string> getDatabasePath, TemplateQueries templateQueries)
    {
        _getDatabasePath = getDatabasePath;
        _templateQueries = templateQueries;

        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var databasePath = _getDatabasePath();
        var rows = _templateQueries.GetAll(databasePath);

        Templates.Clear();
        foreach (var row in rows)
        {
            Templates.Add(row);
        }

        SelectedTemplate = Templates.FirstOrDefault(t => t.Id == SelectedTemplate?.Id) ?? Templates.FirstOrDefault();
    }

    partial void OnSelectedTemplateChanged(TemplateRow? value)
    {
        if (value is not null)
        {
            IsCreatingNew = false;
            SetEditorText?.Invoke(value.Command);

            // Só depois da primeira carga (ver EnsureInitialPeriodAsync) — trocar o combo
            // durante a construção do ViewModel (Refresh() no construtor) dispararia
            // ResultReady antes de existir alguém escutando (MainWindow assina o evento só
            // depois que `new TemplatesViewModel(...)` retorna).
            if (_periodAutoInitialized)
            {
                ExecutarComando(value.Command);
            }
        }

        ExcluirTemplateCommand.NotifyCanExecuteChanged();
        SalvarAlteracoesCommand.NotifyCanExecuteChanged();
    }

    private bool _periodAutoInitialized;

    // Primeira vez que a tela abre: em vez do período padrão fixo (últimos 7 dias, que fica
    // vazio se os dados existentes forem mais antigos — foi exatamente isso que causou "não
    // apareceu nenhum registro" logo depois de restaurar o banco de um teste anterior), fecha
    // De/Até no intervalo real dos dados e já executa o template selecionado. Mesmo padrão de
    // ChartViewModel.EnsureInitialPeriodAsync (Eventos de Picos). Só roda uma vez por sessão.
    public async Task EnsureInitialPeriodAsync()
    {
        if (_periodAutoInitialized)
        {
            return;
        }

        _periodAutoInitialized = true;

        var (min, max) = _templateQueries.GetOverallDateRange(_getDatabasePath());
        if (min is { } minValue && max is { } maxValue)
        {
            PeriodFrom = minValue.ToLocalTime().Date;
            PeriodTo = maxValue.ToLocalTime().Date;
        }

        if (SelectedTemplate is { } selected)
        {
            ExecutarComando(selected.Command);
        }
    }

    [RelayCommand]
    private async Task ExecutarAsync()
    {
        if (GetEditorTextAsync is null)
        {
            return;
        }

        var command = await GetEditorTextAsync();
        ExecutarComando(command);
    }

    // Compartilhado por ExecutarAsync (lê o SQL editado do WebView2) e pelos disparos
    // automáticos — troca de template no combo (OnSelectedTemplateChanged) e carga inicial
    // (EnsureInitialPeriodAsync) — que já sabem o texto de antemão (TemplateRow.Command) e
    // não precisam de round-trip com o editor só pra reobter o que já têm em mãos.
    private void ExecutarComando(string command)
    {
        var databasePath = _getDatabasePath();
        var from = new DateTimeOffset((PeriodFrom ?? DateTime.Today.AddDays(-7)).Date);
        var to = new DateTimeOffset((PeriodTo ?? DateTime.Today).Date.AddDays(1).AddTicks(-1));

        try
        {
            var (columns, rows) = _templateQueries.ExecuteReadOnly(databasePath, command, from, to);
            _lastResult = new QueryResult(columns, rows);
            StatusText = $"{rows.Count} linha(s), {columns.Count} coluna(s).";
            ResultReady?.Invoke(this, _lastResult);
        }
        catch (Exception ex)
        {
            _lastResult = null;
            StatusText = $"Erro: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportarCsv()
    {
        if (_lastResult is null || _lastResult.Rows.Count == 0)
        {
            StatusText = "Nada pra exportar — execute uma consulta primeiro.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{(SelectedTemplate?.Name ?? "template")}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        CsvExporter.ExportQueryResult(dialog.FileName, _lastResult.Columns, _lastResult.Rows);
        StatusText = $"Exportado pra {dialog.FileName}";
    }

    [RelayCommand]
    private void AlternarEditor() => IsEditorVisible = !IsEditorVisible;

    [RelayCommand]
    private void IniciarNovoTemplate()
    {
        IsCreatingNew = true;
        IsEditorVisible = true;
        NewTemplateName = string.Empty;
        NewTemplateError = string.Empty;
        SetEditorText?.Invoke("SELECT *\nFROM AlertEvents\nWHERE TimestampUtc >= $from AND TimestampUtc <= $to\nORDER BY TimestampUtc DESC;");
    }

    [RelayCommand]
    private void CancelarNovoTemplate()
    {
        IsCreatingNew = false;
        NewTemplateError = string.Empty;

        if (SelectedTemplate is { } selected)
        {
            SetEditorText?.Invoke(selected.Command);
        }
    }

    // O nome é o único critério checável de forma síncrona (CanExecute desabilita o botão em
    // tempo real); o SQL só dá pra validar no clique, já que o texto mora no editor (WebView2)
    // e é lido via ExecuteScriptAsync, não fica espelhado aqui — daí NewTemplateError abaixo.
    // Não basta o texto começar com SELECT: só salva se a consulta rodar de verdade contra o
    // banco (ExecutarComando), com o período atual — evita salvar um template que nunca foi
    // sequer carregado uma vez e que na prática dá erro (coluna/tabela errada, etc).
    [RelayCommand(CanExecute = nameof(PodeSalvarNovoTemplate))]
    private async Task SalvarNovoTemplateAsync()
    {
        if (GetEditorTextAsync is null)
        {
            return;
        }

        var command = await GetEditorTextAsync();
        ExecutarComando(command);

        if (_lastResult is null)
        {
            NewTemplateError = StatusText;
            return;
        }

        var id = PermanentDatabase.InsertTemplate(_getDatabasePath(), NewTemplateName.Trim(), command, defaultParameters: null);

        IsCreatingNew = false;
        NewTemplateError = string.Empty;
        Refresh();
        SelectedTemplate = Templates.FirstOrDefault(t => t.Id == id);
        StatusText = "Template salvo.";
    }

    private bool PodeSalvarNovoTemplate() => !string.IsNullOrWhiteSpace(NewTemplateName);

    partial void OnNewTemplateNameChanged(string value) => SalvarNovoTemplateCommand.NotifyCanExecuteChanged();

    // Salva o SQL editado de volta no template já selecionado (em vez de criar um novo) —
    // só pra templates do usuário, mesma restrição de ExcluirTemplate. Nome/DefaultParameters
    // ficam como estavam; só o Command muda. Mesma exigência de SalvarNovoTemplateAsync: só
    // salva se a consulta rodar de verdade (ExecutarComando), não só "parece um SELECT".
    [RelayCommand(CanExecute = nameof(PodeSalvarAlteracoes))]
    private async Task SalvarAlteracoesAsync()
    {
        if (GetEditorTextAsync is null || SelectedTemplate is not { IsBuiltIn: false } selected)
        {
            return;
        }

        var command = await GetEditorTextAsync();
        ExecutarComando(command);

        if (_lastResult is null)
        {
            return;
        }

        PermanentDatabase.UpdateTemplate(_getDatabasePath(), selected.Id, selected.Name, command, selected.DefaultParameters);

        Refresh();
        SelectedTemplate = Templates.FirstOrDefault(t => t.Id == selected.Id);
        StatusText = "Alterações salvas.";
    }

    private bool PodeSalvarAlteracoes() => SelectedTemplate is { IsBuiltIn: false };

    // Templates padrão (IsBuiltIn) não têm botão de excluir — o próprio DeleteTemplate no
    // Core já ignora IsBuiltIn como segunda camada de proteção, mas nem chega a oferecer a
    // opção na UI pra esses.
    [RelayCommand(CanExecute = nameof(PodeExcluirTemplate))]
    private void ExcluirTemplate()
    {
        if (SelectedTemplate is not { IsBuiltIn: false } selected)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Excluir o template \"{selected.Name}\"? Essa ação não pode ser desfeita.",
            "Excluir template",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        PermanentDatabase.DeleteTemplate(_getDatabasePath(), selected.Id);
        Refresh();
        StatusText = "Template excluído.";
    }

    private bool PodeExcluirTemplate() => SelectedTemplate is { IsBuiltIn: false };
}
