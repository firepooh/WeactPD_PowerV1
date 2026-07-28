using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PdPower.Core.Models;

namespace PdPower.App.Controls;

/// <summary>
/// 듀얼 Y축 시계열 차트 — 좌축 전압, 우축 전류. x축은 인덱스가 아니라 <b>시각</b>이다.
/// </summary>
/// <remarks>
/// 점이 화면 폭보다 많으면 픽셀 열마다 최소/최대를 뽑아 수직선으로 그린다(min/max 데시메이션).
/// 1시간 창의 14,400점을 700 px 에 균등 샘플링으로 넣으면 스파이크가 사라지는데,
/// 전원 장치 파형에서 그 스파이크가 정작 보고 싶은 것이다.
///
/// 재렌더는 자체 타이머로 묶는다. 백그라운드 폴링이 10 ms 마다 점을 넣으므로
/// 이벤트마다 InvalidateVisual 하면 초당 100회 렌더가 걸린다.
/// </remarks>
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

    /// <summary>렌더 상한 ≈ 16 fps. 폴링 주기와 무관하게 이 속도로만 다시 그린다.</summary>
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(60);

    private readonly DispatcherTimer _renderTimer;
    private bool _dirty;

    public TrendChart()
    {
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = RenderInterval };
        _renderTimer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            InvalidateVisual();
        };
        _renderTimer.Start();
    }

    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(MeasurementHistory), typeof(TrendChart),
        new PropertyMetadata(null, OnHistoryChanged));

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

    public MeasurementHistory? History
    {
        get => (MeasurementHistory?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
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

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;

        if (e.OldValue is MeasurementHistory old) old.Updated -= chart.OnHistoryUpdated;
        if (e.NewValue is MeasurementHistory added) added.Updated += chart.OnHistoryUpdated;

        chart._dirty = true;
    }

    /// <summary>백그라운드 폴링 스레드에서 올라온다 — 플래그만 세우고 렌더는 타이머에 맡긴다.</summary>
    private void OnHistoryUpdated(object? sender, EventArgs e) => _dirty = true;

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

        var history = History;
        var now = DateTime.Now;
        var window = history?.Window ?? TimeSpan.FromMinutes(1);
        var samples = history?.Snapshot(now) ?? [];

        double voltageMax = NiceCeiling(samples.Length > 0 ? samples.Max(s => s.Volts) : 0, 20.0, 5.0);
        double currentMax = NiceCeiling(samples.Length > 0 ? samples.Max(s => s.Amps) : 0, 3.0, 0.5);

        DrawGrid(dc, plot, voltageMax, currentMax);

        if (samples.Length >= 2)
        {
            DrawSeries(dc, plot, samples, now, window, s => s.Volts, voltageMax, VoltageBrush);
            DrawSeries(dc, plot, samples, now, window, s => s.Amps, currentMax, CurrentBrush);
        }
        else
        {
            var hint = FormatText(samples.Length == 0 ? "waiting for samples" : "collecting…", 10);
            dc.DrawText(hint, new Point(
                plot.X + (plot.Width - hint.Width) / 2,
                plot.Y + (plot.Height - hint.Height) / 2));
        }

        DrawTimeLabels(dc, plot, now, window);
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

    /// <summary>
    /// 점이 픽셀 열 수보다 많으면 열마다 최소/최대를 수직선으로, 적으면 폴리라인으로 그린다.
    /// </summary>
    private void DrawSeries(
        DrawingContext dc, Rect plot, MeasurementSample[] samples, DateTime now, TimeSpan window,
        Func<MeasurementSample, double> selector, double max, Brush brush)
    {
        var pen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round };
        int columns = Math.Max(1, (int)plot.Width);

        double XOf(DateTime t)
        {
            double age = (now - t).TotalMilliseconds;
            double ratio = 1 - Math.Clamp(age / window.TotalMilliseconds, 0, 1);
            return plot.X + plot.Width * ratio;
        }

        double YOf(double value) => plot.Bottom - plot.Height * Math.Clamp(value / max, 0, 1);

        if (samples.Length <= columns)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (int i = 0; i < samples.Length; i++)
                {
                    var point = new Point(XOf(samples[i].Timestamp), YOf(selector(samples[i])));
                    if (i == 0) ctx.BeginFigure(point, isFilled: false, isClosed: false);
                    else ctx.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            return;
        }

        // min/max 데시메이션: 열마다 세로 범위를 긋고, 열 사이는 평균으로 이어 추세를 남긴다
        var spine = new StreamGeometry();
        using (var ctx = spine.Open())
        {
            bool started = false;
            int index = 0;

            for (int column = 0; column < columns; column++)
            {
                double columnRight = plot.X + (column + 1) * plot.Width / columns;

                double min = double.MaxValue, maxValue = double.MinValue;
                int taken = 0;
                while (index < samples.Length && XOf(samples[index].Timestamp) <= columnRight)
                {
                    double value = selector(samples[index]);
                    min = Math.Min(min, value);
                    maxValue = Math.Max(maxValue, value);
                    taken++;
                    index++;
                }

                if (taken == 0) continue;

                double x = plot.X + (column + 0.5) * plot.Width / columns;
                double yMin = YOf(min), yMax = YOf(maxValue);
                if (Math.Abs(yMin - yMax) > 0.5) dc.DrawLine(pen, new Point(x, yMin), new Point(x, yMax));

                var mid = new Point(x, (yMin + yMax) / 2);
                if (!started) { ctx.BeginFigure(mid, isFilled: false, isClosed: false); started = true; }
                else ctx.LineTo(mid, isStroked: true, isSmoothJoin: true);
            }
        }
        spine.Freeze();
        dc.DrawGeometry(null, pen, spine);
    }

    private void DrawTimeLabels(DrawingContext dc, Rect plot, DateTime now, TimeSpan window)
    {
        // 창이 길면 초 단위는 의미가 없다
        string format = window >= TimeSpan.FromMinutes(10) ? "HH:mm" : "HH:mm:ss";

        var start = FormatText((now - window).ToString(format, CultureInfo.InvariantCulture), 10);
        var end = FormatText(now.ToString(format, CultureInfo.InvariantCulture), 10);

        dc.DrawText(start, new Point(plot.X, plot.Bottom + 5));
        dc.DrawText(end, new Point(plot.Right - end.Width, plot.Bottom + 5));
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
