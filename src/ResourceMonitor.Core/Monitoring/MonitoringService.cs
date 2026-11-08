using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Diagnostics;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Monitoring;

// Loop de monitoramento extraído do console pra ser reutilizado pela GUI (Start/Stop
// em vez de rodar do início ao fim de um processo). Roda em Task.Run — os eventos
// disparam numa thread de pool, quem assinar de uma UI precisa marshalar de volta.
public sealed class MonitoringService : IDisposable
{
    // Falhas seguidas toleradas antes de desistir: um ciclo que falha (banco ocupado, disco que
    // sumiu no meio da leitura...) é só pulado; várias seguidas indicam problema que não se
    // resolve sozinho, aí o loop para e avisa via Faulted.
    private const int MaxConsecutiveTickFailures = 5;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _stopRequested;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public event EventHandler<ResourceSample>? SampleCollected;
    public event EventHandler<AlertEvent>? AlertRaised;
    public event EventHandler<DiskSpaceWarning>? DiskSpaceLow;

    // Um ciclo falhou, mas o loop segue tentando no próximo.
    public event EventHandler<Exception>? TickFailed;

    // O loop desistiu (falha na inicialização ou MaxConsecutiveTickFailures seguidas) e parou.
    public event EventHandler<Exception>? Faulted;

    // Dispara em Start()/StopAsync() independente de quem chamou — o menu da bandeja
    // (App.xaml.cs) chama esses métodos direto, sem passar pelo MonitoringViewModel, então
    // sem isso os botões Iniciar/Parar da janela ficavam com o estado errado depois de
    // iniciar/parar pela bandeja. Também dispara quando o loop para sozinho por falha, já com
    // IsRunning = false.
    public event EventHandler? RunningStateChanged;

    public void Start(MonitorSettings settings, string dataDirectory)
    {
        if (IsRunning)
        {
            return;
        }

        _stopRequested = false;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var loopTask = Task.Run(() => RunLoopAsync(settings, dataDirectory, token));
        _loopTask = loopTask;

        // Loop que termina sozinho (falha) não passa por StopAsync — avisa daqui. Num Stop
        // normal quem avisa é o próprio StopAsync, por isso a checagem de _stopRequested.
        loopTask.ContinueWith(
            _ =>
            {
                if (!_stopRequested)
                {
                    RunningStateChanged?.Invoke(this, EventArgs.Empty);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        RunningStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        if (_cts is null || _loopTask is null)
        {
            return;
        }

        _stopRequested = true;
        _cts.Cancel();
        try
        {
            await _loopTask;
        }
        catch (Exception)
        {
            // Falhas do loop já foram reportadas via Faulted.
        }

        _cts.Dispose();
        _cts = null;
        _loopTask = null;
        RunningStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunLoopAsync(MonitorSettings settings, string dataDirectory, CancellationToken token)
    {
        var logDirectory = Path.Combine(dataDirectory, settings.LogDirectory);
        var databasePath = Path.Combine(logDirectory, "resourcemonitor.db");

        DiskMonitor? diskMonitor = null;
        RecentSamplesBuffer? recentSamples = null;
        PermanentDatabase? permanent = null;
        EventCaptureCoordinator? captureCoordinator = null;

        // Alertas com Start já gravado mas ainda sem End — se o app for encerrado com algo
        // aberto aqui (Parar manual, crash), esses IDs são marcados como interrompidos no finally.
        var openAlerts = new Dictionary<string, long>();

        try
        {
            // Inicialização dentro do try: uma falha aqui (banco travado, sem permissão de
            // escrita...) chega no Faulted em vez de deixar o app dizendo que está monitorando.
            diskMonitor = new DiskMonitor();
            var sampler = new ResourceSampler(diskMonitor);
            var thresholdMonitor = new ThresholdMonitor(settings);

            // Guarda só o necessário pra janela antes/depois de um pico, com folga de 2 intervalos.
            recentSamples = new RecentSamplesBuffer(TimeSpan.FromSeconds(
                settings.PreEventSeconds + settings.PostEventSeconds + (2 * settings.SampleIntervalSeconds)));
            permanent = new PermanentDatabase(databasePath);

            // Corrige alertas deixados abertos por uma execução anterior que não encerrou
            // normalmente (processo morto/kill) — sem isso ficariam "Em andamento" pra sempre.
            permanent.MarkOrphanedAlertsInterrupted();

            captureCoordinator = new EventCaptureCoordinator(settings);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.SampleIntervalSeconds));

            // Ponto diário (CPU/RAM/IO/espaço em disco): reaproveita a amostra que o tick já
            // calculou, sem sampling extra — só captura de ~5 em 5min, não a cada tick.
            var dailyAggregateInterval = TimeSpan.FromMinutes(5);
            DateTimeOffset? lastDailyAggregateSample = null;
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

            var consecutiveFailures = 0;

            do
            {
                try
                {
                    var sample = sampler.Sample(settings.ExcludedProcesses);
                    if (sample is null)
                    {
                        continue; // amostra de aquecimento
                    }

                    recentSamples.Add(sample);

                    SampleCollected?.Invoke(this, sample);

                    var events = thresholdMonitor.Evaluate(sample);
                    foreach (var alertEvent in events)
                    {
                        var alertEventId = permanent.InsertAlertEvent(alertEvent);
                        var key = $"{alertEvent.Metric}|{alertEvent.DriveName}";

                        if (alertEvent.EventType == AlertEventType.Start)
                        {
                            openAlerts[key] = alertEventId;

                            var (topByCpu, topByRam, topByIo) = sampler.GetTopProcesses(settings.TopProcessCount);
                            permanent.InsertProcessSnapshots(alertEventId, "Cpu", topByCpu);
                            permanent.InsertProcessSnapshots(alertEventId, "Ram", topByRam);
                            permanent.InsertProcessSnapshots(alertEventId, "Io", topByIo);

                            captureCoordinator.BeginCapture(alertEventId, alertEvent.PeakTimestamp ?? alertEvent.Timestamp);
                        }
                        else
                        {
                            openAlerts.Remove(key);
                        }

                        AlertRaised?.Invoke(this, alertEvent);
                    }

                    // Heartbeat: enquanto o alerta segue ativo, atualiza o último instante confirmado.
                    foreach (var openAlertId in openAlerts.Values)
                    {
                        permanent.UpdateLastActive(openAlertId, sample.Timestamp);
                    }

                    // Espaço em disco é notificação pontual, não episódio — não grava nada.
                    foreach (var warning in thresholdMonitor.EvaluateDiskFreeSpace(sample))
                    {
                        DiskSpaceLow?.Invoke(this, warning);
                    }

                    if (lastDailyAggregateSample is null || sample.Timestamp - lastDailyAggregateSample >= dailyAggregateInterval)
                    {
                        var today = DateOnly.FromDateTime(sample.Timestamp.ToLocalTime().Date);
                        var systemDisk = sample.Disks.FirstOrDefault(d => string.Equals(d.DriveName, systemDrive, StringComparison.OrdinalIgnoreCase));
                        permanent.UpsertDailyAggregate(
                            today, sample.CpuRawPercent, sample.RamRawPercent, systemDisk?.IoPercent ?? 0, systemDisk?.FreePercent ?? 0, systemDrive);
                        lastDailyAggregateSample = sample.Timestamp;
                    }

                    captureCoordinator.FlushReady(sample.Timestamp, recentSamples, permanent);
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaxConsecutiveTickFailures)
                    {
                        throw;
                    }

                    ErrorLog.Write("Monitoramento", $"Falha num ciclo de amostragem ({consecutiveFailures} seguida(s)), tentando de novo.", ex);
                    TickFailed?.Invoke(this, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // parada solicitada via StopAsync
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Monitoramento", "Monitoramento parado por erro.", ex);
            Faulted?.Invoke(this, ex);
        }
        finally
        {
            try
            {
                if (recentSamples is not null && permanent is not null)
                {
                    captureCoordinator?.FlushAll(recentSamples, permanent);

                    foreach (var openAlertId in openAlerts.Values)
                    {
                        permanent.MarkInterrupted(openAlertId, DateTimeOffset.UtcNow);
                    }
                }
            }
            catch (Exception ex)
            {
                // Banco indisponível no encerramento: os alertas abertos ficam como órfãos e
                // são corrigidos no próximo Start (MarkOrphanedAlertsInterrupted).
                ErrorLog.Write("Monitoramento", "Falha ao fechar as capturas no encerramento.", ex);
            }

            permanent?.Dispose();
            diskMonitor?.Dispose();
        }
    }

    public void Dispose()
    {
        _stopRequested = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
