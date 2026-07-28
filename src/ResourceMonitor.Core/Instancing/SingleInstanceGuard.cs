using System.Threading;

namespace ResourceMonitor.Instancing;

// Mutex nomeado em vez de lock file: o Windows libera automaticamente se o processo dono
// morrer por qualquer motivo (crash, taskkill /F), sem precisar de lógica de recuperação.
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "ResourceMonitor.SingleInstance";

    private readonly Mutex _mutex;

    public bool IsPrimaryInstance { get; }

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public void Dispose()
    {
        if (IsPrimaryInstance)
        {
            // Só quem criou o mutex é dono; liberar sem ser dono lança SynchronizationLockException.
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
