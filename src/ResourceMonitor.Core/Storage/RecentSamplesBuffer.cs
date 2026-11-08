using ResourceMonitor.Sampling;

namespace ResourceMonitor.Storage;

// Amostras dos últimos instantes, só em memória, pra montar a janela antes/depois de um pico
// (ver EventCaptureCoordinator). Substitui o antigo SQLite em memória: guarda poucos minutos e
// é usado só pelo loop de monitoramento (uma thread), então uma lista simples basta.
public sealed class RecentSamplesBuffer
{
    private readonly TimeSpan _window;
    private readonly List<ResourceSample> _samples = new();

    public RecentSamplesBuffer(TimeSpan window)
    {
        _window = window;
    }

    public int Count => _samples.Count;

    // Amostras chegam em ordem de tempo; ao entrar uma nova, sai o que ficou mais velho que a janela.
    public void Add(ResourceSample sample)
    {
        _samples.Add(sample);

        var cutoff = sample.Timestamp - _window;
        var expired = 0;
        while (expired < _samples.Count && _samples[expired].Timestamp < cutoff)
        {
            expired++;
        }

        if (expired > 0)
        {
            _samples.RemoveRange(0, expired);
        }
    }

    // Limites inclusivos, como o BETWEEN do cache antigo.
    public List<ResourceSample> GetRange(DateTimeOffset start, DateTimeOffset end) =>
        _samples.Where(s => s.Timestamp >= start && s.Timestamp <= end).ToList();
}
