using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ResourceMonitor.Gui.Controls;

// Gráfico de linhas desenhado direto no WPF (substitui o antigo chart.html num WebView2, que
// custava um processo de navegador por gráfico). Eixo Y fixo em 0–100%, eixo X espaçado por
// índice (um ponto por amostra ou por dia), legenda no topo, marcadores clicáveis opcionais e,
// ao passar o mouse, linha vertical com os valores do ponto.
public sealed class LineChart : FrameworkElement
{
    private const double PaddingLeft = 50;
    private const double PaddingRight = 20;
    private const double PaddingTop = 24;
    private const double PaddingBottom = 40;
    private const double MarkerRadius = 5;
    private const double MarkerHitRadius = 9;

    private static readonly Typeface Font = new("Segoe UI");
    private static readonly Typeface BoldFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));
    private static readonly Brush LegendTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), 1));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)), 1));
    private static readonly Pen TooltipBorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xC3, 0xC8, 0xD1)), 1));
    private static readonly Pen MarkerBorderPen = Freeze(new Pen(Brushes.White, 2));

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ChartData), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(LineChart),
        new FrameworkPropertyMetadata("Sem dados pra exibir.", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxXLabelsProperty = DependencyProperty.Register(
        nameof(MaxXLabels), typeof(int), typeof(LineChart),
        new FrameworkPropertyMetadata(10, FrameworkPropertyMetadataOptions.AffectsRender));

    // Posição de cada marcador desenhado, refeita a cada OnRender — usada no clique e no cursor.
    private readonly List<(Point Center, ChartMarker Marker)> _markerHitboxes = new();
    private int? _hoverIndex;

    public ChartData? Data
    {
        get => (ChartData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public int MaxXLabels
    {
        get => (int)GetValue(MaxXLabelsProperty);
        set => SetValue(MaxXLabelsProperty, value);
    }

    // Clique num marcador (ex: Eventos de picos abre o detalhe do episódio).
    public event EventHandler<ChartMarker>? MarkerClicked;

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private int PointCount => Data?.XLabels.Count ?? 0;

    private double PlotWidth => ActualWidth - PaddingLeft - PaddingRight;

    private double PlotHeight => ActualHeight - PaddingTop - PaddingBottom;

    private double XForIndex(int index) =>
        PaddingLeft + (PointCount > 1 ? (double)index / (PointCount - 1) : 0) * PlotWidth;

    private double YForValue(double percent) =>
        PaddingTop + PlotHeight - (Math.Clamp(percent, 0, 100) / 100) * PlotHeight;

    private FormattedText Text(string text, double size, Brush brush, Typeface? typeface = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface ?? Font, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        _markerHitboxes.Clear();

        // Fundo branco também faz o controle receber o mouse em toda a área.
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));

        var data = Data;
        if (data is null || data.XLabels.Count == 0 || data.Series.Count == 0)
        {
            dc.DrawText(Text(EmptyText, 14, TextBrush), new Point(20, 16));
            return;
        }

        if (PlotWidth <= 0 || PlotHeight <= 0)
        {
            return;
        }

        DrawGridAndAxes(dc, data);
        foreach (var series in data.Series)
        {
            DrawSeries(dc, series);
        }

        DrawLegend(dc, data);
        if (data.Markers is { Count: > 0 } markers)
        {
            DrawMarkers(dc, data, markers);
        }

        if (data.Footnote is { } footnote)
        {
            dc.DrawText(Text(footnote, 12, TextBrush), new Point(PaddingLeft, ActualHeight - 18));
        }

        if (_hoverIndex is { } hoverIndex && hoverIndex < data.XLabels.Count)
        {
            DrawHover(dc, data, hoverIndex);
        }
    }

    private void DrawGridAndAxes(DrawingContext dc, ChartData data)
    {
        // Meio pixel de deslocamento: linha de 1 px fica nítida em vez de borrada em 2 px.
        for (var percent = 0; percent <= 100; percent += 20)
        {
            var y = Math.Round(YForValue(percent)) + 0.5;
            dc.DrawLine(GridPen, new Point(PaddingLeft, y), new Point(PaddingLeft + PlotWidth, y));
            var label = Text($"{percent}%", 11, TextBrush);
            dc.DrawText(label, new Point(10, y - label.Height / 2));
        }

        var step = Math.Max(1, data.XLabels.Count / Math.Max(1, MaxXLabels));
        for (var i = 0; i < data.XLabels.Count; i += step)
        {
            dc.DrawText(Text(data.XLabels[i], 11, TextBrush), new Point(XForIndex(i) - 15, ActualHeight - 28));
        }
    }

    private void DrawSeries(DrawingContext dc, ChartSeries series)
    {
        if (series.Values.Count == 0)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(XForIndex(0), YForValue(series.Values[0])), isFilled: false, isClosed: false);
            for (var i = 1; i < series.Values.Count; i++)
            {
                context.LineTo(new Point(XForIndex(i), YForValue(series.Values[i])), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        var pen = new Pen(new SolidColorBrush(series.Color), 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawLegend(DrawingContext dc, ChartData data)
    {
        var x = PaddingLeft;
        foreach (var series in data.Series)
        {
            dc.DrawRectangle(new SolidColorBrush(series.Color), null, new Rect(x, 4, 10, 10));
            var label = Text(series.Name, 11, LegendTextBrush);
            dc.DrawText(label, new Point(x + 14, 9 - label.Height / 2));
            x += 14 + label.Width + 20;
        }
    }

    // Ponto sobre a própria linha da métrica, no valor daquele dia; rótulo acima dele. Rótulo que
    // bateria em outro já desenhado (mesmo dia ou dias vizinhos) sobe até achar espaço; se chegar
    // no topo do gráfico, procura abaixo do ponto.
    private void DrawMarkers(DrawingContext dc, ChartData data, IReadOnlyList<ChartMarker> markers)
    {
        var placedLabels = new List<Rect>();
        foreach (var marker in markers)
        {
            if (marker.SeriesIndex < 0 || marker.SeriesIndex >= data.Series.Count)
            {
                continue;
            }

            var series = data.Series[marker.SeriesIndex];
            var index = Math.Clamp(marker.Index, 0, data.XLabels.Count - 1);
            var value = index < series.Values.Count ? series.Values[index] : 0;
            var center = new Point(XForIndex(index), YForValue(value));
            var brush = new SolidColorBrush(series.Color);

            dc.DrawEllipse(brush, MarkerBorderPen, center, MarkerRadius, MarkerRadius);
            _markerHitboxes.Add((center, marker));

            var label = Text(marker.Label, 11, brush);
            var labelX = Math.Min(center.X + 8, ActualWidth - label.Width - 4);
            var box = FindFreeLabelBox(placedLabels, labelX - 2, center.Y, label.Width + 4, label.Height);
            placedLabels.Add(box);
            dc.DrawRectangle(Brushes.White, null, box);
            dc.DrawText(label, new Point(box.X + 2, box.Y));
        }
    }

    private Rect FindFreeLabelBox(List<Rect> placed, double left, double pointY, double width, double height)
    {
        const double step = 4;
        bool IsFree(Rect candidate) => !placed.Any(p => p.IntersectsWith(candidate));

        for (var top = pointY - 20; top >= PaddingTop; top -= step)
        {
            var candidate = new Rect(left, top, width, height);
            if (IsFree(candidate))
            {
                return candidate;
            }
        }

        for (var top = pointY + 8; top + height <= PaddingTop + PlotHeight; top += step)
        {
            var candidate = new Rect(left, top, width, height);
            if (IsFree(candidate))
            {
                return candidate;
            }
        }

        // Sem espaço livre nenhum (gráfico muito baixo e cheio de picos): fica acima do ponto mesmo.
        return new Rect(left, Math.Max(PaddingTop, pointY - 20), width, height);
    }

    private void DrawHover(DrawingContext dc, ChartData data, int index)
    {
        var x = Math.Round(XForIndex(index)) + 0.5;
        dc.DrawLine(HoverPen, new Point(x, PaddingTop), new Point(x, PaddingTop + PlotHeight));

        var lines = new List<(FormattedText Text, Brush? Swatch)>
        {
            (Text(data.XLabels[index], 12, LegendTextBrush, BoldFont), null),
        };

        foreach (var series in data.Series)
        {
            if (index >= series.Values.Count)
            {
                continue;
            }

            var brush = new SolidColorBrush(series.Color);
            dc.DrawEllipse(brush, MarkerBorderPen, new Point(x, YForValue(series.Values[index])), 3.5, 3.5);
            lines.Add((Text($"{series.Name}: {series.Values[index]:N1}%", 12, LegendTextBrush), brush));
        }

        const double padding = 8;
        const double swatchSpace = 14;
        var width = lines.Max(l => l.Text.Width + (l.Swatch is null ? 0 : swatchSpace)) + padding * 2;
        var height = lines.Sum(l => l.Text.Height) + padding * 2;

        // À direita da linha; se não couber, à esquerda.
        var left = x + 12;
        if (left + width > ActualWidth - 4)
        {
            left = x - 12 - width;
        }

        left = Math.Max(4, left);
        var top = PaddingTop + 4;
        dc.DrawRoundedRectangle(Brushes.White, TooltipBorderPen, new Rect(left, top, width, height), 3, 3);

        var lineTop = top + padding;
        foreach (var (text, swatch) in lines)
        {
            var textLeft = left + padding;
            if (swatch is not null)
            {
                dc.DrawRectangle(swatch, null, new Rect(textLeft, lineTop + text.Height / 2 - 4, 8, 8));
                textLeft += swatchSpace;
            }

            dc.DrawText(text, new Point(textLeft, lineTop));
            lineTop += text.Height;
        }
    }

    private ChartMarker? HitTestMarker(Point position)
    {
        foreach (var (center, marker) in _markerHitboxes)
        {
            var dx = position.X - center.X;
            var dy = position.Y - center.Y;
            if (dx * dx + dy * dy <= MarkerHitRadius * MarkerHitRadius)
            {
                return marker;
            }
        }

        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        Cursor = HitTestMarker(position) is not null ? Cursors.Hand : null;

        int? hoverIndex = null;
        if (PointCount > 0 && PlotWidth > 0
            && position.X >= PaddingLeft - 10 && position.X <= PaddingLeft + PlotWidth + 10
            && position.Y >= PaddingTop && position.Y <= PaddingTop + PlotHeight)
        {
            var relative = (position.X - PaddingLeft) / PlotWidth;
            hoverIndex = PointCount > 1 ? (int)Math.Round(Math.Clamp(relative, 0, 1) * (PointCount - 1)) : 0;
        }

        if (hoverIndex != _hoverIndex)
        {
            _hoverIndex = hoverIndex;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex is not null)
        {
            _hoverIndex = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (HitTestMarker(e.GetPosition(this)) is { } marker)
        {
            MarkerClicked?.Invoke(this, marker);
            e.Handled = true;
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height);
}
