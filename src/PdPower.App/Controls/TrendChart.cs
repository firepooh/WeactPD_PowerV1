using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PdPower.App.ViewModels;

namespace PdPower.App.Controls;

/// <summary>
/// 듀얼 Y축 시계열 차트 — 좌축 전압, 우축 전류.
/// 샘플 수가 64개로 고정이라 직접 그리는 편이 차트 라이브러리보다 가볍고 디자인에 정확히 맞는다.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    private const double LeftAxisWidth = 38;
    private const double RightAxisWidth = 34;
    private const double TopPadding = 6;
    private const double BottomPadding = 18;
    private const int GridLevels = 5;

    /// <summary>오토스케일 후보 배수 — 스펙의 1 / 2 / 2.5 / 5 단위.</summary>
    private static readonly double[] Mantissas = [1, 2, 2.5, 5];

    private static readonly Typeface LabelTypeface = new("Consolas");

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IEnumerable<MeasurementSample>), typeof(TrendChart),
        new PropertyMetadata(null, OnSamplesChanged));

    public static readonly DependencyProperty VoltageBrushProperty = DependencyProperty.Register(
        nameof(VoltageBrush), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentBrushProperty = DependencyProperty.Register(
        nameof(CurrentBrush), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.OrangeRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AxisLabelBrushProperty = DependencyProperty.Register(
        nameof(AxisLabelBrush), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<MeasurementSample>? Samples
    {
        get => (IEnumerable<MeasurementSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public Brush VoltageBrush
    {
        get => (Brush)GetValue(VoltageBrushProperty);
        set => SetValue(VoltageBrushProperty, value);
    }

    public Brush CurrentBrush
    {
        get => (Brush)GetValue(CurrentBrushProperty);
        set => SetValue(CurrentBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush AxisLabelBrush
    {
        get => (Brush)GetValue(AxisLabelBrushProperty);
        set => SetValue(AxisLabelBrushProperty, value);
    }

    /// <summary>컬렉션이 통째로 교체될 때 구독을 옮긴다.</summary>
    private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;

        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= chart.OnCollectionChanged;

        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += chart.OnCollectionChanged;

        chart.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= LeftAxisWidth + RightAxisWidth + 8 || height <= TopPadding + BottomPadding + 8) return;

        var plot = new Rect(
            LeftAxisWidth,
            TopPadding,
            width - LeftAxisWidth - RightAxisWidth,
            height - TopPadding - BottomPadding);

        var samples = Samples?.ToList() ?? [];
        double voltageMax = NiceCeiling(samples.Count > 0 ? samples.Max(s => s.Volts) : 0, 20.0, 5.0);
        double currentMax = NiceCeiling(samples.Count > 0 ? samples.Max(s => s.Amps) : 0, 3.0, 0.5);

        DrawGrid(dc, plot, voltageMax, currentMax);

        if (samples.Count >= 2)
        {
            DrawSeries(dc, plot, samples, s => s.Volts, voltageMax, VoltageBrush);
            DrawSeries(dc, plot, samples, s => s.Amps, currentMax, CurrentBrush);
            DrawTimeLabels(dc, plot, samples);
        }
        else
        {
            var hint = FormatText(samples.Count == 0 ? "waiting for samples" : "collecting…", 10);
            dc.DrawText(hint, new Point(
                plot.X + (plot.Width - hint.Width) / 2,
                plot.Y + (plot.Height - hint.Height) / 2));
        }
    }

    private void DrawGrid(DrawingContext dc, Rect plot, double voltageMax, double currentMax)
    {
        // 헤어라인은 물리 픽셀에 맞춰야 흐릿하게 번지지 않는다.
        var pen = new Pen(GridBrush, 1);
        double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int level = 0; level <= GridLevels; level++)
        {
            double ratio = (double)level / GridLevels;
            double y = Math.Round((plot.Bottom - ratio * plot.Height) * dpiScale) / dpiScale;

            dc.DrawLine(pen, new Point(plot.X, y), new Point(plot.Right, y));

            var leftLabel = FormatText(FormatTick(voltageMax * ratio, voltageMax), 10);
            dc.DrawText(leftLabel, new Point(plot.X - 6 - leftLabel.Width, y - leftLabel.Height / 2));

            var rightLabel = FormatText(FormatTick(currentMax * ratio, currentMax), 10);
            dc.DrawText(rightLabel, new Point(plot.Right + 6, y - rightLabel.Height / 2));
        }
    }

    private void DrawSeries(
        DrawingContext dc, Rect plot, List<MeasurementSample> samples,
        Func<MeasurementSample, double> selector, double max, Brush brush)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < samples.Count; i++)
            {
                double x = plot.X + plot.Width * i / (samples.Count - 1);
                double y = plot.Bottom - plot.Height * Math.Clamp(selector(samples[i]) / max, 0, 1);

                if (i == 0) ctx.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                else ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
            }
        }
        geometry.Freeze();

        dc.DrawGeometry(null, new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round }, geometry);
    }

    private void DrawTimeLabels(DrawingContext dc, Rect plot, List<MeasurementSample> samples)
    {
        var first = FormatText(samples[0].Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture), 10);
        var last = FormatText(samples[^1].Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture), 10);

        dc.DrawText(first, new Point(plot.X, plot.Bottom + 5));
        dc.DrawText(last, new Point(plot.Right - last.Width, plot.Bottom + 5));
    }

    private FormattedText FormatText(string text, double size) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, size,
        AxisLabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>축 상한을 1/2/2.5/5 계열에서 고른다. 피크가 없으면 <paramref name="fallback"/>.</summary>
    private static double NiceCeiling(double peak, double cap, double fallback)
    {
        if (peak <= 0) return fallback;

        double exponent = Math.Floor(Math.Log10(peak));
        for (double scale = exponent; scale <= exponent + 2; scale++)
        {
            double magnitude = Math.Pow(10, scale);
            foreach (double mantissa in Mantissas)
            {
                double candidate = mantissa * magnitude;
                if (candidate >= peak) return Math.Min(candidate, cap);
            }
        }

        return cap;
    }

    /// <summary>축 상한이 작을 때만 소수점을 보여 눈금이 0/0/0/0 으로 뭉개지지 않게 한다.</summary>
    private static string FormatTick(double value, double max)
        => value.ToString(max <= 3 ? "0.0" : "0", CultureInfo.InvariantCulture);
}
