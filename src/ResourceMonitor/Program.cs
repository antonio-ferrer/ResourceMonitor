using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Monitoring;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var settings = AppSettingsStore.Load();
var dataDirectory = AppSettingsStore.GetDataDirectory();

using var monitoringService = new MonitoringService();

var stopSignal = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopSignal.TrySetResult();
};

monitoringService.SampleCollected += (_, sample) =>
{
    Console.WriteLine(
        $"[{sample.Timestamp:HH:mm:ss}] CPU {sample.CpuRawPercent,5:F1}% (líquido {sample.CpuAdjustedPercent,5:F1}%) | " +
        $"RAM {sample.RamRawPercent,5:F1}% (líquido {sample.RamAdjustedPercent,5:F1}%)");
};

monitoringService.AlertRaised += (_, alertEvent) =>
{
    var driveTag = alertEvent.DriveName is null ? string.Empty : $"({alertEvent.DriveName}) ";
    if (alertEvent.EventType == AlertEventType.Start)
    {
        Console.WriteLine(
            $"  >> ALERTA: {alertEvent.Metric} {driveTag}= {alertEvent.RawValue:F1} (limite {alertEvent.Threshold:F1})");
    }
    else
    {
        Console.WriteLine($"  >> RECUPERADO: {alertEvent.Metric} {driveTag}");
    }
};

monitoringService.Faulted += (_, ex) =>
{
    Console.Error.WriteLine($"Erro no monitoramento: {ex}");
};

Console.WriteLine("ResourceMonitor iniciado. Ctrl+C para encerrar.");
Console.WriteLine($"Intervalo: {settings.SampleIntervalSeconds}s | Dados: {dataDirectory}");
Console.WriteLine();

monitoringService.Start(settings, dataDirectory);

await stopSignal.Task;

await monitoringService.StopAsync();

Console.WriteLine("ResourceMonitor encerrado.");
