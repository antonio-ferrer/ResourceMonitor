using System.Threading;

namespace ResourceMonitor.Gui;

// Canal só entre instâncias da GUI: a instância primária escuta, qualquer instância perdedora
// sinaliza. Se quem estiver segurando o SingleInstanceGuard for o console, o Set() abaixo não
// tem ninguém escutando — inofensivo. Sempre usa o construtor (nunca OpenExisting): create-or-open
// é atômico, então não existe corrida de "quem chega primeiro".
public sealed class GuiActivationSignal : IDisposable
{
    private const string EventName = "ResourceMonitor.Gui.ShowRequest";

    private readonly EventWaitHandle _showRequested;
    private readonly ManualResetEvent _stopping = new(initialState: false);

    public GuiActivationSignal()
    {
        _showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, EventName, out _);
    }

    public void RequestActivation() => _showRequested.Set();

    // Roda em thread de pool (Task.Run), não na UI thread — quem chama onActivationRequested
    // deve fazer o próprio Dispatcher.Invoke.
    public void RunListenerLoop(Action onActivationRequested)
    {
        var handles = new WaitHandle[] { _showRequested, _stopping };
        while (true)
        {
            var signaled = WaitHandle.WaitAny(handles);
            if (signaled == 1)
            {
                return;
            }

            try
            {
                onActivationRequested();
            }
            catch
            {
                // Best-effort: nunca deixa a thread de escuta morrer por causa de um Dispatcher
                // já em processo de shutdown.
            }
        }
    }

    public void Dispose()
    {
        _stopping.Set();
        _showRequested.Dispose();
        _stopping.Dispose();
    }
}
