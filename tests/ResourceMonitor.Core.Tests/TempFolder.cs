using Microsoft.Data.Sqlite;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Core.Tests;

// Pasta temporária por teste, apagada no fim — os testes nunca tocam no banco nem nas
// configurações reais em %LOCALAPPDATA%\ResourceMonitor.
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "ResourceMonitor.Tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    // Banco novo com schema e templates padrão, já fechado.
    public string CreateDatabase(string name = "test.db")
    {
        var databasePath = File(name);
        using (new PermanentDatabase(databasePath))
        {
        }

        return databasePath;
    }

    public void Dispose()
    {
        // O pool do Microsoft.Data.Sqlite segura o arquivo aberto depois do Dispose da conexão.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
