using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ResourceMonitor.Gui.Converters;
using ResourceMonitor.Monitoring;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Gui.ViewModels;

public partial class ChartViewModel : ObservableObject
{
    private readonly Func<string> _getDatabasePath;
    private readonly AlertEventQueries _alertEventQueries;

    [ObservableProperty] private string statusText = "Selecione um evento na aba Dados.";
    [ObservableProperty] private string liveStatusText = "Aguardando amostras...";
    [ObservableProperty] private string trendStatusText = "Tendência diária (últimos 30 dias)";

    // Filtro da seção Eventos de Picos (Gráficos > Eventos de Picos) — mesmo padrão de
    // ReportViewModel (período + checkboxes por métrica).
    [ObservableProperty] private DateTime? periodFrom = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime? periodTo = DateTime.Today;
    [ObservableProperty] private bool includeCpu = true;
    [ObservableProperty] private bool includeRam = true;
    [ObservableProperty] private bool includeDiscoIo = true;
    [ObservableProperty] private string eventosStatusText = string.Empty;

    // Popup de detalhe do episódio (clique num marcador de "Eventos de Picos") — só a métrica
    // clicada, não os três kinds; já formatado em texto pra não precisar de converter na XAML.
    [ObservableProperty] private string popupMetricLabel = string.Empty;
    [ObservableProperty] private string popupPeriodLabel = string.Empty;
    public ObservableCollection<string> PopupProcessLines { get; } = new();

    public event EventHandler<string>? LiveSamplesReady;
    public event EventHandler<string>? DailyTrendReady;
    public event EventHandler<string>? TrendWithEventsReady;

    public ChartViewModel(LiveMonitorService liveMonitor, Func<string> getDatabasePath, AlertEventQueries alertEventQueries)
    {
        _getDatabasePath = getDatabasePath;
        _alertEventQueries = alertEventQueries;

        // Fonte independente de Iniciar/Parar — ver LiveMonitorService. Compartilhada com a
        // Home (mesmo mecanismo, sem amostragem duplicada).
        liveMonitor.SnapshotUpdated += OnLiveSnapshotUpdated;
    }

    private bool _eventosPeriodInitialized;

    // Primeira vez que "Eventos de Picos" é aberta: em vez do período padrão fixo (últimos 7
    // dias, que pode não ter nenhuma captura ainda), fecha De/Até no intervalo real dos dados
    // existentes e já carrega — evita a tela abrir "0 eventos" só porque o usuário não mexeu
    // no filtro. Só roda uma vez por sessão (mesmo padrão de inicialização preguiçosa dos
    // WebView2 em MainWindow.xaml.cs); reaberturas seguintes respeitam o filtro que o usuário
    // já ajustou.
    public async Task EnsureInitialPeriodAsync()
    {
        if (_eventosPeriodInitialized)
        {
            return;
        }

        _eventosPeriodInitialized = true;

        var databasePath = _getDatabasePath();
        var allDays = _alertEventQueries.GetDailyAggregates(databasePath, null, null);
        if (allDays.Count > 0)
        {
            PeriodFrom = allDays[0].Date.ToDateTime(TimeOnly.MinValue);
            PeriodTo = allDays[^1].Date.ToDateTime(TimeOnly.MinValue);
        }

        await CarregarEventosDePicosAsync();
    }

    [RelayCommand]
    private void LoadDailyTrend()
    {
        var databasePath = _getDatabasePath();
        var to = DateOnly.FromDateTime(DateTime.Today);
        var from = to.AddDays(-30);

        var rows = _alertEventQueries.GetDailyAggregates(databasePath, from, to);

        TrendStatusText = rows.Count == 0
            ? "Tendência diária: sem capturas ainda (aguarde ~5min de monitoramento)."
            : $"Tendência diária: {rows.Count} dia(s) nos últimos 30.";

        // Disco vira "em uso" (100 - livre) pra seguir a mesma direção das outras 3 linhas
        // (subiu = mais consumo), em vez de misturar com "espaço livre" (subiu = bom).
        var payload = rows.Select(r => new
        {
            date = r.Date.ToString("dd/MM"),
            cpu = Math.Round(r.AvgCpuRawPercent, 1),
            ram = Math.Round(r.AvgRamRawPercent, 1),
            io = Math.Round(r.AvgIoPercent, 1),
            diskUsage = Math.Round(100 - r.AvgDiskFreePercent, 1),
        });

        DailyTrendReady?.Invoke(this, JsonSerializer.Serialize(payload));
    }

    // Combina a tendência diária com os eventos de alerta do período, pra responder "o pico
    // aconteceu durante uma tendência de alta, ou foi isolado?". 4 cenários (ver plano):
    // tendência+eventos, só eventos (usa uma leitura atual como referência), só tendência,
    // nenhum dos dois (idem à leitura atual, sem marcadores) — nunca fica em branco.
    [RelayCommand]
    private async Task CarregarEventosDePicosAsync()
    {
        var databasePath = _getDatabasePath();
        var effectiveFrom = (PeriodFrom ?? DateTime.Today.AddDays(-7)).Date;
        var effectiveTo = (PeriodTo ?? DateTime.Today).Date;

        var trend = _alertEventQueries.GetDailyAggregates(
            databasePath, DateOnly.FromDateTime(effectiveFrom), DateOnly.FromDateTime(effectiveTo));

        var selectedMetrics = new HashSet<string>();
        if (IncludeCpu) selectedMetrics.Add("CPU");
        if (IncludeRam) selectedMetrics.Add("RAM");
        if (IncludeDiscoIo) selectedMetrics.Add("DiscoIO");

        var from = new DateTimeOffset(effectiveFrom);
        var to = new DateTimeOffset(effectiveTo.AddDays(1).AddTicks(-1));
        var episodes = _alertEventQueries.GetAlertEpisodes(databasePath, from, to)
            .Where(e => selectedMetrics.Contains(e.Metric))
            .ToList();

        object trendPayload;
        object eventsPayload;
        bool usingCurrentAsBaseline;

        if (trend.Count > 0)
        {
            var dayIndexByDate = trend
                .Select((row, index) => (row.Date, index))
                .ToDictionary(x => x.Date, x => x.index);

            trendPayload = trend.Select(r => new
            {
                date = r.Date.ToString("dd/MM"),
                cpu = Math.Round(r.AvgCpuRawPercent, 1),
                ram = Math.Round(r.AvgRamRawPercent, 1),
                io = Math.Round(r.AvgIoPercent, 1),
                diskUsage = Math.Round(100 - r.AvgDiskFreePercent, 1),
            }).ToList();

            // Episódio fora dos dias com tendência registrada (ex: início de monitoramento
            // no meio do período) não tem onde ser desenhado — fica de fora do marcador.
            // Agrupado por (dia, métrica) — vários alertas da mesma métrica no mesmo dia viram
            // um marcador só com contagem no label, em vez de pontos empilhados sem explicação.
            eventsPayload = episodes
                .Select(e => new { Episode = e, Date = DateOnly.FromDateTime(e.Timestamp.LocalDateTime) })
                .Where(x => dayIndexByDate.ContainsKey(x.Date))
                .GroupBy(x => (DayIndex: dayIndexByDate[x.Date], x.Episode.Metric))
                .Select(g => new
                {
                    dayIndex = g.Key.DayIndex,
                    metric = g.Key.Metric,
                    label = FormatMarkerLabel(g.Key.Metric, g.Count(), GetTopOffenderNames(databasePath, g.Select(x => x.Episode.StartEventId), g.Key.Metric)),
                    // Episódios já vêm ordenados do mais recente pro mais antigo (ver GetAlertEpisodes)
                    // — clicar na bolinha abre o mais recente do grupo quando há mais de um no dia.
                    alertEventId = g.First().Episode.StartEventId,
                    timestamp = g.First().Episode.Timestamp.ToString("O"),
                    durationMinutes = g.First().Episode.DurationMinutes,
                    isInterrupted = g.First().Episode.IsInterrupted,
                })
                .ToList();

            usingCurrentAsBaseline = false;
        }
        else
        {
            var current = await SampleCurrentAsync();
            trendPayload = new[] { current };
            eventsPayload = episodes
                .GroupBy(e => e.Metric)
                .Select(g => new
                {
                    dayIndex = 0,
                    metric = g.Key,
                    label = FormatMarkerLabel(g.Key, g.Count(), GetTopOffenderNames(databasePath, g.Select(e => e.StartEventId), g.Key)),
                    alertEventId = g.First().StartEventId,
                    timestamp = g.First().Timestamp.ToString("O"),
                    durationMinutes = g.First().DurationMinutes,
                    isInterrupted = g.First().IsInterrupted,
                })
                .ToList();
            usingCurrentAsBaseline = true;
        }

        var payload = new { trend = trendPayload, events = eventsPayload, usingCurrentAsBaseline };

        EventosStatusText = episodes.Count == 0
            ? "Nenhum evento encontrado pro período/recursos selecionados."
            : $"{episodes.Count} evento(s) no período.";

        TrendWithEventsReady?.Invoke(this, JsonSerializer.Serialize(payload));
    }

    private static string FormatMarkerLabel(string metric, int count, IReadOnlyList<string> topOffenders)
    {
        var name = metric switch
        {
            "CPU" => "CPU",
            "RAM" => "RAM",
            "DiscoIO" => "Disco I/O",
            _ => metric,
        };

        var countSuffix = count > 1 ? $" ({count})" : "";
        var appsSuffix = topOffenders.Count switch
        {
            0 => "",
            1 => $" — {topOffenders[0]}",
            2 => $" — {topOffenders[0]}, {topOffenders[1]}",
            _ => $" — {topOffenders[0]}, {topOffenders[1]} +{topOffenders.Count - 2}",
        };

        return $"{name}{countSuffix}{appsSuffix}";
    }

    // O app que mais pesava nessa métrica em cada episódio do grupo (mesmo snapshot gravado na
    // hora do alerta, já usado no "Detalhe do episódio") — sem repetir nome quando o mesmo app
    // aparece em mais de um episódio do mesmo marcador.
    private string[] GetTopOffenderNames(string databasePath, IEnumerable<long> startEventIds, string metric)
    {
        var kind = metric switch { "CPU" => "Cpu", "RAM" => "Ram", "DiscoIO" => "Io", _ => null };
        if (kind is null)
        {
            return Array.Empty<string>();
        }

        return startEventIds
            .Select(id => _alertEventQueries.GetProcessSnapshotsForAlertEvent(databasePath, id)
                .Where(s => s.Kind == kind)
                .OrderByDescending(s => kind switch
                {
                    "Cpu" => s.CpuPercent,
                    "Ram" => s.RamMb,
                    "Io" => s.IoKbPerSec,
                    _ => 0,
                })
                .FirstOrDefault())
            .Where(top => top is not null)
            .Select(top => top!.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Leitura avulsa (não a janela contínua do LiveMonitorService — granularidade incompatível
    // com um eixo por dia) usada como referência quando não há tendência registrada pro período.
    private static async Task<object> SampleCurrentAsync()
    {
        var sampler = new ResourceSampler(new DiskMonitor());
        sampler.Sample(Array.Empty<string>());
        await Task.Delay(1100);
        var sample = sampler.Sample(Array.Empty<string>());

        if (sample is null)
        {
            return new { date = "agora", cpu = 0.0, ram = 0.0, io = 0.0, diskUsage = 0.0 };
        }

        var disk = sample.Disks.Count > 0 ? sample.Disks[0] : null;
        return new
        {
            date = "agora",
            cpu = Math.Round(sample.CpuRawPercent, 1),
            ram = Math.Round(sample.RamRawPercent, 1),
            io = disk is null ? 0.0 : Math.Round(disk.IoPercent, 1),
            diskUsage = disk is null ? 0.0 : Math.Round(100 - disk.FreePercent, 1),
        };
    }

    private void OnLiveSnapshotUpdated(object? sender, LiveSnapshot snapshot)
    {
        var json = ChartJsonFormatter.ToChartJson(snapshot.Samples);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            LiveStatusText = $"{snapshot.Samples.Count} amostra(s) (últimos ~60s).";
            LiveSamplesReady?.Invoke(this, json);
        });
    }

    // Popup de "Eventos de Picos": só os processos que colaboraram pra métrica específica do
    // marcador clicado (ex: clicou num pico de RAM, lista só quem mais consumia RAM), não os
    // três kinds — o clique já diz qual métrica interessa.
    public void LoadForAlertEvent(long alertEventId, string metric, DateTimeOffset timestamp, double? durationMinutes, bool isInterrupted)
    {
        var databasePath = _getDatabasePath();
        var kind = metric switch { "CPU" => "Cpu", "RAM" => "Ram", "DiscoIO" => "Io", _ => metric };
        PopupMetricLabel = metric switch { "CPU" => "CPU", "RAM" => "RAM", "DiscoIO" => "Disco I/O", _ => metric };
        PopupPeriodLabel = FormatPeriodLabel(timestamp, durationMinutes, isInterrupted);

        var snapshots = _alertEventQueries.GetProcessSnapshotsForAlertEvent(databasePath, alertEventId)
            .Where(s => s.Kind == kind)
            .ToList();

        // Mesmo padrão do monitor ao vivo (GetTopProcessesGrouped): soma por nome em vez de
        // listar cada PID separado — "claude" aparecendo 3 vezes só confunde, sem trazer
        // informação extra pra quem está lendo o popup. Sem o rollup de processo pai (ver
        // ResourceSampler.ResolveAttributionName), que precisa da árvore de processos viva —
        // não dá pra reconstruir isso de um snapshot histórico persistido.
        var grouped = snapshots
            .GroupBy(s => s.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Name = g.Key,
                Count = g.Count(),
                Cpu = g.Sum(s => s.CpuPercent),
                Ram = g.Sum(s => s.RamMb),
                Io = g.Sum(s => s.IoKbPerSec),
            })
            .OrderByDescending(g => kind switch
            {
                "Cpu" => g.Cpu,
                "Ram" => g.Ram,
                "Io" => g.Io,
                _ => 0,
            })
            .ToList();

        PopupProcessLines.Clear();
        foreach (var g in grouped)
        {
            var valueText = kind switch
            {
                "Cpu" => $"{g.Cpu:N1}%",
                "Ram" => $"{g.Ram:N0} MB",
                "Io" => $"{g.Io:N0} KB/s",
                _ => "",
            };
            var nameText = g.Count > 1 ? $"{g.Name} ({g.Count})" : g.Name;
            PopupProcessLines.Add($"{nameText} — {valueText}");
        }

        StatusText = grouped.Count == 0
            ? $"Evento #{alertEventId}: sem processos capturados pra {PopupMetricLabel} (janela ainda pendente ou fora do intervalo)."
            : $"Evento #{alertEventId}: {grouped.Count} processo(s) por {PopupMetricLabel}.";
    }

    // Mesmo padrão de texto do DurationMinutesDisplayConverter ("Em andamento" / "maior que X min"
    // pra episódios interrompidos), aplicado ao intervalo completo (início–fim) do popup.
    private static string FormatPeriodLabel(DateTimeOffset timestamp, double? durationMinutes, bool isInterrupted)
    {
        var start = timestamp.ToLocalTime();
        var startText = start.ToString("dd/MM/yyyy HH:mm");

        if (durationMinutes is not { } minutes)
        {
            return $"{startText} — em andamento";
        }

        var end = start.AddMinutes(minutes);
        var durationText = $"{minutes:N0} min";
        return isInterrupted
            ? $"{startText} – {end:HH:mm} (maior que {durationText}, interrompido)"
            : $"{startText} – {end:HH:mm} ({durationText})";
    }
}
