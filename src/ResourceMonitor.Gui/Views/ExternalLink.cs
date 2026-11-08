using System.Diagnostics;
using System.Windows.Navigation;

namespace ResourceMonitor.Gui.Views;

// Links de texto (crédito do ícone, "Ver no GitHub") abrem no navegador padrão.
internal static class ExternalLink
{
    public static void Open(RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
