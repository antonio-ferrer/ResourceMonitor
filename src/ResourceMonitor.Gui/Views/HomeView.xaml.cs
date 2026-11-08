using System.Windows;
using System.Windows.Navigation;
using UserControl = System.Windows.Controls.UserControl;

namespace ResourceMonitor.Gui.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    // "Editar configurações →": a navegação entre telas é da MainWindow.
    public event EventHandler? SettingsRequested;

    private void OnEditSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnExternalLinkRequestNavigate(object sender, RequestNavigateEventArgs e) => ExternalLink.Open(e);
}
