using System.IO;
using ResourceMonitor.Gui.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace ResourceMonitor.Gui.Views;

public partial class HelpView : UserControl
{
    private bool _initStarted;

    public HelpView()
    {
        InitializeComponent();
    }

    // WebView2 criado só na primeira vez que a Ajuda é aberta.
    public async Task EnsureInitializedAsync(WebViewHost webViewHost)
    {
        if (_initStarted)
        {
            return;
        }

        _initStarted = true;

        var helpHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "ajuda.html")).AbsoluteUri;
        await webViewHost.InitializeAsync(HelpWebView);
        HelpWebView.CoreWebView2.Navigate(helpHtmlUri);
    }
}
