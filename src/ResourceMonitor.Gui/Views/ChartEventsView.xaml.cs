using ResourceMonitor.Gui.Controls;
using ResourceMonitor.Storage;
using UserControl = System.Windows.Controls.UserControl;

namespace ResourceMonitor.Gui.Views;

public partial class ChartEventsView : UserControl
{
    public ChartEventsView()
    {
        InitializeComponent();
    }

    // Clique na bolinha de um marcador: a MainWindow abre o popup com os processos do pico.
    public event EventHandler<AlertEpisodeRow>? EpisodeRequested;

    private void OnEventsChartMarkerClicked(object? sender, ChartMarker marker)
    {
        if (marker.Tag is AlertEpisodeRow episode)
        {
            EpisodeRequested?.Invoke(this, episode);
        }
    }
}
