using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ResourceMonitor.Gui.ViewModels;

public sealed record CurrentSampleRow(
    DateTimeOffset Timestamp,
    double CpuRawPercent,
    double CpuAdjustedPercent,
    double RamRawPercent,
    double RamAdjustedPercent);

public partial class DataViewModel : ObservableObject
{
    private readonly MonitoringService _monitoringService;
    private readonly Func<string> _getDatabasePath;

    [ObservableProperty] private DateTime? fromDate;
    [ObservableProperty] private DateTime? toDate;
    [ObservableProperty] private AlertEventRow? selectedEvent;
    [ObservableProperty] private string statusText = string.Empty;

    public ObservableCollection<CurrentSampleRow> CurrentSamples { get; } = new();

    public ObservableCollection<AlertEventRow> Events { get; } = new();

    public event EventHandler<long>? ViewChartRequested;

    public DataViewModel(MonitoringService monitoringService, Func<string> getDatabasePath)
    {
        _monitoringService = monitoringService;
        _getDatabasePath = getDatabasePath;

        _monitoringService.SampleCollected += OnSampleCollected;

        Refresh();
    }

    private void OnSampleCollected(object? sender, ResourceSample sample)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentSamples.Clear();
            foreach (var s in _monitoringService.GetCurrentSamples())
            {
                CurrentSamples.Add(new CurrentSampleRow(
                    s.Timestamp, s.CpuRawPercent, s.CpuAdjustedPercent, s.RamRawPercent, s.RamAdjustedPercent));
            }
        });
    }

    [RelayCommand]
    private void Refresh()
    {
        var databasePath = _getDatabasePath();
        DateTimeOffset? from = FromDate is { } f ? new DateTimeOffset(f) : null;
        DateTimeOffset? to = ToDate is { } t ? new DateTimeOffset(t.Date.AddDays(1).AddTicks(-1)) : null;

        var rows = AlertEventQueries.GetAlertEvents(databasePath, from, to);

        Events.Clear();
        foreach (var row in rows)
        {
            Events.Add(row);
        }

        StatusText = $"{Events.Count} evento(s) na base de picos.";
    }

    [RelayCommand]
    private void ExportCsv()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"alertas_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        CsvExporter.ExportAlertEvents(dialog.FileName, Events);
        StatusText = $"Exportado pra {dialog.FileName}";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ViewChart()
    {
        if (SelectedEvent is { } selected)
        {
            ViewChartRequested?.Invoke(this, selected.Id);
        }
    }

    private bool HasSelection() => SelectedEvent is not null;

    partial void OnSelectedEventChanged(AlertEventRow? value) => ViewChartCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void ClearData()
    {
        if (_monitoringService.IsRunning)
        {
            MessageBox.Show(
                "Pare o monitoramento antes de apagar os dados.",
                "ResourceMonitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            "Isso apaga permanentemente todos os eventos, snapshots e amostras da base de picos. Continuar?",
            "Apagar dados",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        PermanentDatabase.ClearAllData(_getDatabasePath());
        Refresh();
        StatusText = "Base de picos apagada.";
    }
}
