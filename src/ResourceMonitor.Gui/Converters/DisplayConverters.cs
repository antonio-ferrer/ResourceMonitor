using System.Globalization;
using System.Windows.Data;
using ResourceMonitor.Storage;

namespace ResourceMonitor.Gui.Converters;

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
