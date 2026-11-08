using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ResourceMonitor.Gui.Services;

// Todo WebView2 do app (hoje só Relatórios e Ajuda — os gráficos são nativos, ver
// Controls/LineChart) passa por aqui: ambiente compartilhado e registro pra conseguir
// suspender os que já existem quando a janela sai da vista.
public sealed class WebViewHost
{
    private readonly string _userDataFolder;
    private readonly List<WebView2> _initializedWebViews = new();
    private Task<CoreWebView2Environment>? _environment;
    private bool _inBackground;

    // Pasta de dados em %LOCALAPPDATA%\ResourceMonitor\WebView2. Sem isso o WebView2 cria
    // "<exe>.WebView2" ao lado do executável — suja a pasta do app e falha numa pasta sem
    // permissão de escrita (ex: Program Files).
    public WebViewHost(string userDataFolder)
    {
        _userDataFolder = userDataFolder;
    }

    // Chamado na thread de UI; o ambiente é criado na primeira vez que alguém pede.
    public async Task InitializeAsync(WebView2 webView)
    {
        _environment ??= CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
        await webView.EnsureCoreWebView2Async(await _environment);
        _initializedWebViews.Add(webView);
    }

    // Janela fora da vista (bandeja ou minimizada): pede pros WebView2 já criados liberarem
    // memória. Escondida na bandeja, também suspende — TrySuspendAsync exige o controle
    // invisível, o que minimizado não é. Ao voltar, o WebView2 retoma sozinho quando fica
    // visível; aqui só devolve o nível de memória normal.
    public async Task UpdateBackgroundStateAsync(Window window)
    {
        var inBackground = !window.IsVisible || window.WindowState == WindowState.Minimized;
        if (inBackground == _inBackground)
        {
            return;
        }

        _inBackground = inBackground;
        var targetLevel = inBackground
            ? CoreWebView2MemoryUsageTargetLevel.Low
            : CoreWebView2MemoryUsageTargetLevel.Normal;
        foreach (var webView in _initializedWebViews)
        {
            webView.CoreWebView2.MemoryUsageTargetLevel = targetLevel;
        }

        if (window.IsVisible)
        {
            return;
        }

        // Deixa os próprios controles processarem que ficaram invisíveis antes de suspender.
        await Dispatcher.Yield(DispatcherPriority.Background);
        foreach (var webView in _initializedWebViews.ToList())
        {
            if (window.IsVisible)
            {
                return;
            }

            try
            {
                await webView.CoreWebView2.TrySuspendAsync();
            }
            catch (Exception)
            {
                // Suspender é só economia de memória — se o WebView2 recusar, segue normal.
            }
        }
    }

    // Pasta de dados do WebView2 que versões até a 0.4.0 criavam ao lado do exe. É só cache
    // do próprio app; se não der pra apagar, fica lá.
    public static void DeleteLegacyFolder()
    {
        try
        {
            var legacyFolder = Path.Combine(
                AppContext.BaseDirectory, Path.GetFileName(Environment.ProcessPath ?? "ResourceMonitor.Gui.exe") + ".WebView2");
            if (Directory.Exists(legacyFolder))
            {
                Directory.Delete(legacyFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
