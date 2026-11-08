using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceMonitor.Gui.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace ResourceMonitor.Gui.Views;

// Pedido pra abrir o detalhe de um pico a partir de uma linha do resultado (duplo-clique).
public sealed record AlertEventRequest(
    long AlertEventId, string Metric, DateTimeOffset Timestamp, double? DurationMinutes, bool IsInterrupted);

public partial class TemplatesView : UserControl
{
    private TemplatesViewModel? _viewModel;

    public TemplatesView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public event EventHandler<AlertEventRequest>? AlertEventRequested;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ResultReady -= OnTemplateResultReady;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as TemplatesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ResultReady += OnTemplateResultReady;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    // Tab insere 4 espaços em vez de mover o foco pra fora do editor — sem isso digitar SQL
    // com indentação vira impossível.
    private void OnSqlEditorPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        SqlEditorTextBox.SelectedText = "    ";
        SqlEditorTextBox.CaretIndex = SqlEditorTextBox.SelectionStart + SqlEditorTextBox.SelectionLength;
        e.Handled = true;
    }

    // RowDefinition não herda DataContext de forma confiável no WPF ({Binding} direto na
    // altura da linha simplesmente não reagia à troca do toggle) — então o toggle do editor
    // (botão "Editor") ajusta a altura das linhas diretamente aqui, via PropertyChanged do
    // ViewModel, em vez de um binding de XAML.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TemplatesViewModel.IsEditorVisible) || _viewModel is null)
        {
            return;
        }

        var visible = _viewModel.IsEditorVisible;
        EditorRow.Height = new GridLength(visible ? 180 : 0);
        EditorSplitterRow.Height = new GridLength(visible ? 6 : 0);
    }

    // Colunas não são conhecidas em tempo de compilação (resultado de SQL arbitrário) —
    // DataGridTextColumn construída aqui em vez de XAML. Binding por índice — cada linha é um
    // IReadOnlyList<string?> (ver TemplateQueries.ExecuteReadOnly), que expõe indexador
    // compatível com o binding do WPF.
    private void OnTemplateResultReady(object? sender, QueryResult result)
    {
        TemplateResultsGrid.Columns.Clear();
        for (var i = 0; i < result.Columns.Count; i++)
        {
            TemplateResultsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = result.Columns[i],
                Binding = new System.Windows.Data.Binding($"[{i}]"),
            });
        }

        TemplateResultsGrid.ItemsSource = result.Rows;
    }

    // Caso especial: se o resultado tiver StartEventId+Metric (como o template "Base de
    // picos" sempre tem), duplo-clique na linha pede o mesmo popup de detalhe do pico que o
    // clique num marcador de Gráficos > Eventos de Picos abre.
    private void OnTemplateResultsGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TemplateResultsGrid.SelectedItem is not IReadOnlyList<string?> row)
        {
            return;
        }

        var columns = TemplateResultsGrid.Columns;
        var startEventIdIndex = FindColumnIndex(columns, "StartEventId");
        var metricIndex = FindColumnIndex(columns, "Metric");
        if (startEventIdIndex < 0 || metricIndex < 0)
        {
            return;
        }

        if (!long.TryParse(row[startEventIdIndex], out var alertEventId))
        {
            return;
        }

        var metric = row[metricIndex] ?? string.Empty;

        var timestampIndex = FindColumnIndex(columns, "StartTimestamp");
        var timestamp = timestampIndex >= 0 && row[timestampIndex] is { } timestampText
            && DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTimestamp)
            ? parsedTimestamp
            : DateTimeOffset.UtcNow;

        var durationIndex = FindColumnIndex(columns, "DurationMinutes");
        double? durationMinutes = durationIndex >= 0 && double.TryParse(row[durationIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDuration)
            ? parsedDuration
            : null;

        var interruptedIndex = FindColumnIndex(columns, "Interrupted");
        var isInterrupted = interruptedIndex >= 0 && row[interruptedIndex] is "1" or "True" or "true";

        AlertEventRequested?.Invoke(this, new AlertEventRequest(alertEventId, metric, timestamp, durationMinutes, isInterrupted));
    }

    private static int FindColumnIndex(IEnumerable<DataGridColumn> columns, string name)
    {
        var index = 0;
        foreach (var column in columns)
        {
            if (column.Header is string header && string.Equals(header, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            index++;
        }

        return -1;
    }
}
