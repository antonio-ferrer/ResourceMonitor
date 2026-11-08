using System.IO;
using System.Windows;
using System.Windows.Input;
using ResourceMonitor.Gui.Services;
using ResourceMonitor.Gui.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace ResourceMonitor.Gui.Views;

public partial class ReportView : UserControl
{
    private ReportViewModel? _viewModel;
    private bool _initStarted;
    private bool _webViewReady;

    public ReportView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ReportReady -= OnReportReady;
        }

        _viewModel = e.NewValue as ReportViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ReportReady += OnReportReady;
        }
    }

    // Iniciado só na primeira vez que a tela é aberta (não no Loaded) — inicializar vários
    // WebView2 ao mesmo tempo numa seção ainda não visível causava o conteúdo não renderizar
    // até o usuário navegar por outra seção antes. Aqui a gente espera de verdade o
    // NavigationCompleted (TaskCompletionSource) — é o que permite já disparar "Gerar
    // relatório" com o período padrão (última semana) assim que abre, em vez de esperar o
    // usuário clicar. Cursor de espera cobre a inicialização do WebView2 (a parte que pode
    // demorar de verdade em máquinas mais lentas).
    public async Task EnsureInitializedAsync(WebViewHost webViewHost)
    {
        if (_initStarted)
        {
            return;
        }

        _initStarted = true;
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var reportHtmlUri = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "report.html")).AbsoluteUri;
            var navigationCompleted = new TaskCompletionSource();

            await webViewHost.InitializeAsync(ReportWebView);
            ReportWebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _webViewReady = true;
                navigationCompleted.TrySetResult();
            };
            ReportWebView.CoreWebView2.Navigate(reportHtmlUri);
            await navigationCompleted.Task;

            _viewModel?.GerarRelatorioCommand.Execute(null);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    // Relatório pronto antes da página carregar não é guardado: a geração automática ao abrir
    // (acima) roda depois do NavigationCompleted.
    private void OnReportReady(object? sender, string json)
    {
        if (_webViewReady)
        {
            _ = ReportWebView.ExecuteScriptAsync($"renderReport({json})");
        }
    }
}
