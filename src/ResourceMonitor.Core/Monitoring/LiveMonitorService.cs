using System.Collections.Concurrent;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Monitoring;

public sealed record LiveSnapshot(
    IReadOnlyList<ResourceSample> Samples,
    IReadOnlyList<GroupedProcessUsage> TopByCpu,
    IReadOnlyList<GroupedProcessUsage> TopByRam,
    IReadOnlyList<GroupedProcessUsage> TopByIo);

// Monitor "ao vivo" totalmente separado de MonitoringService: roda independente de
// Iniciar/Parar, amostra a cada 1s, guarda só em memória (nunca grava em cache/banco).
// Compartilhado entre telas (Home, Gráficos) via contagem de consumidores — o timer só roda
// enquanto pelo menos uma tela estiver mostrando o gráfico.
public sealed class LiveMonitorService : IDisposable
{
    private const int WindowSize = 60;
    private const int TopProcessCount = 5;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly ResourceSampler _sampler = new(new DiskMonitor());
    private readonly ConcurrentDictionary<long, ResourceSample> _samples = new();
    private long _sequence;
    private int _consumerCount;
    private Timer? _timer;

    public event EventHandler<LiveSnapshot>? SnapshotUpdated;

    public void AddConsumer()
    {
        if (Interlocked.Increment(ref _consumerCount) == 1)
        {
            StartTimer();
        }
    }

    public void RemoveConsumer()
    {
        if (Interlocked.Decrement(ref _consumerCount) == 0)
        {
            StopTimer();
        }
    }

    private void StartTimer()
    {
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TickInterval);
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
        _samples.Clear(); // efêmero — nada sobrevive fora da janela sendo observada
    }

    private void Tick()
    {
        // Sem padrões de exclusão: a Home mostra a visão geral crua do sistema, não o
        // "líquido" que o monitoramento configurado calcula.
        var sample = _sampler.Sample(Array.Empty<string>());
        if (sample is null)
        {
            return; // aquecimento (primeira amostra não tem delta pra calcular)
        }

        _samples[Interlocked.Increment(ref _sequence)] = sample;

        var cutoff = _sequence - WindowSize;
        foreach (var oldKey in _samples.Keys.Where(k => k <= cutoff))
        {
            _samples.TryRemove(oldKey, out _);
        }

        var ordered = _samples.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        // Subproduto do Sample() acima (WalkProcesses já rastreia todo processo a cada tick,
        // ver ResourceSampler) — não faz nenhuma varredura nova. Agrupado por nome (não por
        // PID) pra não deixar um app com vários processos (Electron/Chromium) ocupar sozinho
        // várias posições do top-N.
        var (topByCpu, topByRam, topByIo) = _sampler.GetTopProcessesGrouped(TopProcessCount);

        SnapshotUpdated?.Invoke(this, new LiveSnapshot(ordered, topByCpu, topByRam, topByIo));
    }

    public void Dispose()
    {
        StopTimer();
    }
}
