namespace ResourceMonitor.Gui.Services;

// "Iniciar com o Windows". Atrás de uma interface pra os testes não mexerem no registro.
public interface IAutoStart
{
    bool IsEnabled();

    void SetEnabled(bool enabled);
}

public sealed class RegistryAutoStart : IAutoStart
{
    public bool IsEnabled() => AutoStartManager.IsEnabled();

    public void SetEnabled(bool enabled) => AutoStartManager.SetEnabled(enabled);
}
