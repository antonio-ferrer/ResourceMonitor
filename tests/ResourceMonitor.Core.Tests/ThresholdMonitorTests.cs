using ResourceMonitor.Alerting;
using ResourceMonitor.Configuration;
using ResourceMonitor.Sampling;

namespace ResourceMonitor.Core.Tests;

public class ThresholdMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static MonitorSettings Settings(int breaches = 3, int recoveries = 3) => new()
    {
        ConsecutiveBreachesToAlert = breaches,
        ConsecutiveRecoveriesToClear = recoveries,
        Thresholds = new ThresholdSettings { CpuPercent = 90, RamPercent = 85, DiskIoPercent = 90 },
    };

    private static ResourceSample Sample(int second, double cpu = 10, double ram = 10, params DiskSample[] disks) =>
        new(T0.AddSeconds(second), cpu, cpu, ram, ram, 16, 8, disks);

    private static DiskSample Disk(string name, double freePercent = 50, double ioPercent = 0) =>
        new(name, freePercent, 100, 200, ioPercent);

    [Fact]
    public void Cpu_alerta_so_depois_de_N_violacoes_seguidas()
    {
        var monitor = new ThresholdMonitor(Settings(breaches: 3));

        Assert.Empty(monitor.Evaluate(Sample(0, cpu: 95)));
        Assert.Empty(monitor.Evaluate(Sample(1, cpu: 95)));
        var events = monitor.Evaluate(Sample(2, cpu: 97));

        var start = Assert.Single(events);
        Assert.Equal(AlertEventType.Start, start.EventType);
        Assert.Equal("CPU", start.Metric);
        Assert.Null(start.DriveName);
    }

    [Fact]
    public void Violacao_interrompida_zera_a_contagem()
    {
        var monitor = new ThresholdMonitor(Settings(breaches: 3));

        monitor.Evaluate(Sample(0, cpu: 95));
        monitor.Evaluate(Sample(1, cpu: 95));
        monitor.Evaluate(Sample(2, cpu: 10));
        monitor.Evaluate(Sample(3, cpu: 95));

        Assert.Empty(monitor.Evaluate(Sample(4, cpu: 95)));
    }

    [Fact]
    public void Alerta_encerra_depois_de_N_recuperacoes_seguidas()
    {
        var monitor = new ThresholdMonitor(Settings(breaches: 1, recoveries: 2));

        Assert.Single(monitor.Evaluate(Sample(0, ram: 95)));
        Assert.Empty(monitor.Evaluate(Sample(1, ram: 10)));
        var end = Assert.Single(monitor.Evaluate(Sample(2, ram: 10)));

        Assert.Equal(AlertEventType.End, end.EventType);
        Assert.Equal("RAM", end.Metric);
    }

    [Fact]
    public void Disco_io_com_duas_unidades_gera_um_evento_sem_unidade()
    {
        var monitor = new ThresholdMonitor(Settings(breaches: 1));

        var events = monitor.Evaluate(Sample(0, disks: [Disk("C:\\", ioPercent: 100), Disk("D:\\", ioPercent: 100)]));

        var start = Assert.Single(events);
        Assert.Equal("DiscoIO", start.Metric);
        Assert.Null(start.DriveName);
    }

    [Fact]
    public void Espaco_livre_baixo_avisa_uma_vez_e_rearma_depois_de_recuperar()
    {
        var settings = Settings(breaches: 1, recoveries: 1);
        settings.Thresholds.DiskFreeThresholds.Add(new DiskThreshold { DriveName = "C:\\", MinFreePercent = 10 });
        var monitor = new ThresholdMonitor(settings);

        var first = monitor.EvaluateDiskFreeSpace(Sample(0, disks: Disk("C:\\", freePercent: 5)));
        var second = monitor.EvaluateDiskFreeSpace(Sample(1, disks: Disk("C:\\", freePercent: 5)));
        monitor.EvaluateDiskFreeSpace(Sample(2, disks: Disk("C:\\", freePercent: 30)));
        var afterRecovery = monitor.EvaluateDiskFreeSpace(Sample(3, disks: Disk("C:\\", freePercent: 5)));

        Assert.Equal("C:\\", Assert.Single(first).DriveName);
        Assert.Empty(second);
        Assert.Single(afterRecovery);
    }

    [Fact]
    public void Disco_configurado_que_sumiu_fica_desligado_ate_o_proximo_start()
    {
        var settings = Settings(breaches: 1);
        settings.Thresholds.DiskFreeThresholds.Add(new DiskThreshold { DriveName = "E:\\", MinFreePercent = 10 });
        var monitor = new ThresholdMonitor(settings);

        Assert.Empty(monitor.EvaluateDiskFreeSpace(Sample(0, disks: Disk("C:\\"))));
        Assert.Empty(monitor.EvaluateDiskFreeSpace(Sample(1, disks: Disk("E:\\", freePercent: 1))));
    }
}
