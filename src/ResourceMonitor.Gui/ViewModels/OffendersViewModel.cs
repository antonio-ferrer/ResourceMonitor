using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Storage;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ResourceMonitor.Gui.ViewModels;

public partial class OffendersViewModel : ObservableObject
{
    private readonly Func<string> _getDatabasePath;
    private readonly AlertEventQueries _alertEventQueries;

    [ObservableProperty] private DateTime? periodFrom = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime? periodTo = DateTime.Today;
    [ObservableProperty] private string statusText = string.Empty;

    public ObservableCollection<TopOffenderRow> Offenders { get; } = new();

    public OffendersViewModel(Func<string> getDatabasePath, AlertEventQueries alertEventQueries)
    {
        _getDatabasePath = getDatabasePath;
        _alertEventQueries = alertEventQueries;

        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        DateTimeOffset? from = PeriodFrom is { } f ? new DateTimeOffset(f) : null;
        DateTimeOffset? to = PeriodTo is { } t ? new DateTimeOffset(t.Date.AddDays(1).AddTicks(-1)) : null;

        var rows = _alertEventQueries.GetTopOffenders(_getDatabasePath(), from, to);

        Offenders.Clear();
        foreach (var row in rows)
        {
            Offenders.Add(row);
        }

        StatusText = $"{Offenders.Count} processo(s) encontrado(s).";
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"ofensores_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        CsvExporter.ExportTopOffenders(dialog.FileName, Offenders);
        StatusText = $"Exportado pra {dialog.FileName}";
    }
}
