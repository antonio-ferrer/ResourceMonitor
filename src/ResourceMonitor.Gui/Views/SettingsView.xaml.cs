using System.Windows.Navigation;
using UserControl = System.Windows.Controls.UserControl;

namespace ResourceMonitor.Gui.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnExternalLinkRequestNavigate(object sender, RequestNavigateEventArgs e) => ExternalLink.Open(e);
}
