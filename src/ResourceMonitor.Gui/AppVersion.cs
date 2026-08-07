using System.Reflection;

namespace ResourceMonitor.Gui;

// AssemblyInformationalVersionAttribute guarda a string exata do <Version> do csproj (ex:
// "0.3.0") — diferente de AssemblyVersion/FileVersion, que o SDK normaliza pra 4 partes
// (0.3.0.0). É o valor certo tanto pro título da janela quanto pra checagem de atualização.
public static class AppVersion
{
    public static string Current { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
