using System.Collections.Concurrent;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Monitoring;

public sealed record LiveSnapshot(
    IReadOnlyList<ResourceSample> Samples,
    IReadOnlyList<GroupedProcessUsage> TopByCpu,
    IReadOnlyList<GroupedProcessUsage> TopByRam,
    IReadOnlyList<GroupedProcessUsage> TopByIo);

// Monitor "ao vivo" totalmente separado de MonitoringService: roda independente de
// Iniciar/Parar, amostra a cada 1s, guarda só em memória (nunca grava em cache/banco).
// Compartilhado entre telas (Home, Gráficos) via contagem de consumidores — o loop só roda
// enquanto pelo menos uma tela estiver mostrando o gráfico.
public sealed class LiveMonitorService : IDisposable
{
    private const int WindowSize = 60;
    private const int TopProcessCount = 5;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly DiskMonitor _diskMonitor = new();
    private readonly ResourceSampler _sampler;
    private readonly ConcurrentDictionary<long, ResourceSample> _samples = new();

    // ResourceSampler não é thread-safe: o lock garante um tick por vez, inclusive quando um
    // Stop+Start rápido deixa o loop antigo terminando o tick dele enquanto o novo começa.
    private readonly object _tickLock = new();

    private long _sequence;
    private int _consumerCount;
    private bool _lastTickFailed;
    private CancellationTokenSource? _loopCts;

    public event EventHandler<LiveSnapshot>? SnapshotUpdated;

    public LiveMonitorService()
    {
        _sampler = new ResourceSampler(_diskMonitor);
    }

    public void AddConsumer()
    {
        if (Interlocked.Increment(ref _consumerCount) == 1)
        {
            StartLoop();
        }
    }

    public void RemoveConsumer()
    {
        if (Interlocked.Decrement(ref _consumerCount) == 0)
        {
            StopLoop();
        }
    }

    // PeriodicTimer não sobrepõe ticks (o próximo só começa depois que o anterior terminou),
    // ao contrário do System.Threading.Timer periódico usado antes.
    private void StartLoop()
    {
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TickInterval);
            try
            {
                do
                {
                    TickSafely(token);
                }
                while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException)
            {
                // StopLoop
            }
        });
    }

    private void StopLoop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        _samples.Clear(); // efêmero — nada sobrevive fora da janela sendo observada
    }

    private void TickSafely(CancellationToken token)
    {
        lock (_tickLock)
        {
            if (token.IsCancellationRequested)
            {
                return; // parado enquanto esperava o lock
            }

            try
            {
                Tick();
                _lastTickFailed = false;
            }
            catch (Exception ex)
            {
                // Tick com falha é só pulado — o próximo tenta de novo. Exceção não tratada aqui
                // (thread de pool) derrubaria o app inteiro. Loga só a primeira de uma sequência,
                // pra uma falha repetida a cada segundo não empurrar o resto do log pra fora.
                if (!_lastTickFailed)
                {
                    ErrorLog.Write("MonitorAoVivo", "Falha num ciclo do monitor ao vivo (pulado).", ex);
                }

                _lastTickFailed = true;
            }
        }
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
        StopLoop();
        lock (_tickLock)
        {
            _diskMonitor.Dispose();
        }
    }
}
