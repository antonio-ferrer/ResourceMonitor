using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ResourceMonitor.Sampling;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Gui.Converters;

// "claude" vira "claude (2)" quando o agrupamento juntou mais de um processo com esse nome
// (ver ResourceSampler.GetTopProcessesGrouped) — só mostra a contagem quando > 1, senão some.
public sealed class GroupedProcessNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        GroupedProcessUsage { InstanceCount: > 1 } grouped => $"{grouped.Name} ({grouped.InstanceCount})",
        GroupedProcessUsage grouped => grouped.Name,
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Pills "Ativa"/"Inativa" do resumo de configurações na Home — mesmas cores do mock aprovado.
public sealed class BoolToActiveLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Ativa" : "Inativa";

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToActiveBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush OnBrush = new(System.Windows.Media.Color.FromRgb(0xEA, 0xF7, 0xEE));
    private static readonly SolidColorBrush OffBrush = new(System.Windows.Media.Color.FromRgb(0xF1, 0xF2, 0xF4));

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? OnBrush : OffBrush;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToActiveForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush OnBrush = new(System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly SolidColorBrush OffBrush = new(System.Windows.Media.Color.FromRgb(0x7C, 0x84, 0x94));

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? OnBrush : OffBrush;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Traduz o código interno da métrica (ver ThresholdMonitor.EvaluateMetric) pro rótulo exibido na grid.
public sealed class MetricDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "DiscoLivre" => "Disco (livre)",
        "DiscoIO" => "Disco (I/O)",
        "CPU" => "CPU",
        "RAM" => "RAM",
        _ => value ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Traduz o Kind de AlertProcessSnapshots (Cpu/Ram/Io) pro rótulo exibido na grid de Ofensores —
// é um conjunto de valores diferente do Metric de AlertEvents (CPU/RAM/DiscoIO/DiscoLivre),
// então não dá pra reaproveitar o MetricDisplayConverter acima.
public sealed class OffenderKindDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "Cpu" => "CPU",
        "Ram" => "RAM",
        "Io" => "Disco (I/O)",
        _ => value ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Opera na linha inteira (não só num campo) pra poder combinar DurationMinutes + IsInterrupted
// num único texto: "3,2 min" (recuperado), "maior que 1,2 min" (interrompido), "Em andamento".
public sealed class DurationMinutesDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AlertEpisodeRow episode)
        {
            return string.Empty;
        }

        if (episode.DurationMinutes is not { } minutes)
        {
            return "Em andamento";
        }

        var formatted = minutes.ToString("N1", culture) + " min";
        return episode.IsInterrupted ? $"maior que {formatted}" : formatted;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Tendência diária guarda espaço LIVRE (ver DailyAggregateRow), mas a exibição segue "consumo"
// (mesma direção de CPU/RAM/I/O: subiu = mais usado) pra não misturar sentidos na mesma grid.
public sealed class DiskUsageFromFreeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double freePercent)
        {
            return (100 - freePercent).ToString("N1", culture) + "%";
        }

        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

// Timestamps são gravados/lidos em UTC (ver PermanentDatabase/CacheDatabase) — esse converter
// passa pra hora local antes de formatar, senão a grid mostra a hora errada pro usuário.
public sealed class LocalDateTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dto)
        {
            var format = parameter as string ?? "dd/MM/yyyy HH:mm:ss";
            return dto.ToLocalTime().ToString(format, culture);
        }

        return value ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
