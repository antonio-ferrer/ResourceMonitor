namespace ResourceMonitor.Gui.Services;

// Diálogos que os ViewModels abrem. Atrás de uma interface pra os testes responderem no lugar
// do usuário (ver ResourceMonitor.Gui.Tests).
public interface IDialogService
{
    // Pergunta Sim/Não com ícone de aviso; true = Sim.
    bool Confirm(string title, string message);

    void ShowWarning(string title, string message);

    // Caminho escolhido, ou null se o usuário cancelou.
    string? PickSaveFile(string filter, string defaultFileName);
}
