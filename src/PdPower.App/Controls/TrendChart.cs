using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PdPower.Core.Models;

namespace PdPower.App.Controls;

/// <summary>
/// 듀얼 Y축 시계열 차트 — 좌축 전압, 우축 전류. x축은 인덱스가 아니라 <b>시각</b>이다.
/// </summary>
/// <remarks>
/// ViewModel 이 잘라준 <see cref="MeasurementWindow"/> 한 장만 그린다. 정지·범위 선택은
/// 전부 그 장을 만드는 쪽의 책임이고, 이 컨트롤은 상태를 갖지 않는다(커서 위치만 예외).
///
/// 점이 화면 폭보다 많으면 픽셀 열마다 최소/최대를 뽑아 수직선으로 그린다(min/max 데시메이션).
/// 균등 샘플링을 하면 전원 장치 파형에서 정작 보고 싶은 스파이크가 사라진다.
/// </remarks>
public sealed class TrendChart : FrameworkElement
{
    // Fit 모드에서는 "11.994" 처럼 자릿수가 늘어나므로 축 폭에 여유를 둔다
    private const double LeftAxisWidth = 46;
    private const double RightAxisWidth = 44;
    private const double TopPadding = 6;
    private const double BandHeight = 6;
    private const double BandGap = 4;
    private const double LabelHeight = 14;
    private const int GridLevels = 5;

    /// <summary>저장 간격의 이 배수 이상 벌어지면 데이터가 없던 구간으로 보고 선을 끊는다.</summary>
    private const double GapFactor = 3;

    /// <summary>오토스케일 후보 배수 — 스펙의 1 / 2 / 2.5 / 5 단위.</summary>
    private static readonly double[] Mantissas = [1, 2, 2.5, 5];

    private static readonly Typeface LabelTypeface = new("Consolas");

    /// <summary>휠 한 칸당 범위 배율.</summary>
    private const double ZoomStep = 1.25;

    /// <summary>수동 범위의 하한·상한. 장치 사양(20 V / 3 A)보다 약간 넉넉하게 둔다.</summary>
    private static readonly (double MinSpan, double MaxSpan, double Ceiling) VoltageLimits = (0.002, 24.0, 24.0);
    private static readonly (double MinSpan, double MaxSpan, double Ceiling) CurrentLimits = (0.001, 3.6, 3.6);

    private double? _cursorX;
    private (double Min, double Max) _manualVoltage = (0, 20);
    private (double Min, double Max) _manualCurrent = (0, 3);

    public TrendChart()
    {
        // 마우스 이벤트를 받으려면 빈 영역도 히트 테스트 대상이어야 한다
        Cursor = Cursors.Cross;
    }

    public static readonly DependencyProperty WindowProperty = DependencyProperty.Register(
        nameof(Window), typeof(MeasurementWindow), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScaleModeProperty = DependencyProperty.Register(
        nameof(ScaleMode), typeof(YScaleMode), typeof(TrendChart),
        new FrameworkPropertyMetadata(YScaleMode.Auto,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsFrozenProperty = DependencyProperty.Register(
        nameof(IsFrozen), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

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

    public static readonly DependencyProperty ReadoutBackgroundProperty = DependencyProperty.Register(
        nameof(ReadoutBackground), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReadoutForegroundProperty = DependencyProperty.Register(
        nameof(ReadoutForeground), typeof(Brush), typeof(TrendChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public MeasurementWindow? Window
    {
        get => (MeasurementWindow?)GetValue(WindowProperty);
        set => SetValue(WindowProperty, value);
    }

    public YScaleMode ScaleMode
    {
        get => (YScaleMode)GetValue(ScaleModeProperty);
        set => SetValue(ScaleModeProperty, value);
    }

    public bool IsFrozen
    {
        get => (bool)GetValue(IsFrozenProperty);
        set => SetValue(IsFrozenProperty, value);
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

    public Brush ReadoutBackground
    {
        get => (Brush)GetValue(ReadoutBackgroundProperty);
        set => SetValue(ReadoutBackgroundProperty, value);
    }

    public Brush ReadoutForeground
    {
        get => (Brush)GetValue(ReadoutForegroundProperty);
        set => SetValue(ReadoutForegroundProperty, value);
    }

    // ── 커서 ─────────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _cursorX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _cursorX = null;
        InvalidateVisual();
    }

    /// <summary>그래프를 클릭하면 정지/재생을 토글한다 (별도 버튼 없음).</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        IsFrozen = !IsFrozen;
        e.Handled = true;
    }

    /// <summary>
    /// 휠로 Y축 범위를 조절한다. 플롯 왼쪽 절반에서는 전압축, 오른쪽 절반에서는 전류축이
    /// 대상이고, 커서가 가리키는 값을 고정점으로 확대·축소한다.
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var plot = ComputePlot();
        if (plot.Width <= 0 || plot.Height <= 0) return;

        var position = e.GetPosition(this);
        bool voltageSide = position.X < plot.X + plot.Width / 2;

        // Auto·Fit 에서 넘어올 때는 지금 보이는 범위를 그대로 물려받아야 튀지 않는다
        if (ScaleMode != YScaleMode.Manual)
        {
            var samples = (Window ?? MeasurementWindow.Empty).Samples;
            _manualVoltage = ComputeAutoOrFit(samples, s => s.Volts, cap: 20.0, fallback: 5.0);
            _manualCurrent = ComputeAutoOrFit(samples, s => s.Amps, cap: 3.0, fallback: 0.5);
            ScaleMode = YScaleMode.Manual;
        }

        var range = voltageSide ? _manualVoltage : _manualCurrent;
        var limits = voltageSide ? VoltageLimits : CurrentLimits;

        // 휠 위 = 확대 = 범위를 좁힌다
        double factor = e.Delta > 0 ? 1 / ZoomStep : ZoomStep;
        double span = Math.Clamp((range.Max - range.Min) * factor, limits.MinSpan, limits.MaxSpan);

        double ratio = Math.Clamp((plot.Bottom - position.Y) / plot.Height, 0, 1);
        double anchor = range.Min + (range.Max - range.Min) * ratio;

        double min = Math.Clamp(anchor - span * ratio, 0, limits.Ceiling - span);
        var zoomed = (min, min + span);

        if (voltageSide) _manualVoltage = zoomed;
        else _manualCurrent = zoomed;

        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>빈 영역에서도 마우스를 받으려면 배경을 칠해야 한다(투명이라도).</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters p)
        => new PointHitTestResult(this, p.HitPoint);

    // ── 렌더 ─────────────────────────────────────────────────────────────

    /// <summary>플롯 사각형. 렌더와 휠 처리가 같은 값을 써야 한다.</summary>
    private Rect ComputePlot()
    {
        double bottomPadding = BandHeight + BandGap + LabelHeight;
        double width = ActualWidth;
        double height = ActualHeight;

        if (width <= LeftAxisWidth + RightAxisWidth + 8 || height <= TopPadding + bottomPadding + 8)
            return Rect.Empty;

        return new Rect(
            LeftAxisWidth,
            TopPadding,
            width - LeftAxisWidth - RightAxisWidth,
            height - TopPadding - bottomPadding);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var plot = ComputePlot();
        if (plot.IsEmpty) return;

        var window = Window ?? MeasurementWindow.Empty;
        var samples = window.Samples;
        var asOf = window.IsEmpty ? DateTime.Now : window.AsOf;
        var span = window.Window;

        var voltageAxis = ResolveAxis(samples, s => s.Volts, cap: 20.0, fallback: 5.0, isVoltage: true);
        var currentAxis = ResolveAxis(samples, s => s.Amps, cap: 3.0, fallback: 0.5, isVoltage: false);

        DrawGrid(dc, plot, voltageAxis, currentAxis);

        if (samples.Length >= 2)
        {
            var gapThreshold = ResolveGapThreshold(window);
            DrawSeries(dc, plot, samples, asOf, span, gapThreshold, s => s.Volts, voltageAxis, VoltageBrush);
            DrawSeries(dc, plot, samples, asOf, span, gapThreshold, s => s.Amps, currentAxis, CurrentBrush);
            DrawStateBand(dc, plot, samples, asOf, span, gapThreshold);
        }
        else
        {
            var hint = FormatText(samples.Length == 0 ? "waiting for samples" : "collecting…", 10);
            dc.DrawText(hint, new Point(
                plot.X + (plot.Width - hint.Width) / 2,
                plot.Y + (plot.Height - hint.Height) / 2));
        }

        DrawTimeLabels(dc, plot, asOf, span);
        DrawBadges(dc, plot);

        if (_cursorX is { } cursorX && samples.Length > 0)
            DrawCursor(dc, plot, samples, asOf, span, cursorX, voltageAxis, currentAxis);
    }

    /// <summary>
    /// 정지·수동 스케일 표시. Freeze 버튼을 없앤 대신 상태를 여기서 알려준다 —
    /// 클릭으로 토글되므로 화면에 표시가 없으면 정지된 줄 모른다.
    /// </summary>
    private void DrawBadges(DrawingContext dc, Rect plot)
    {
        double x = plot.X + 6;

        if (IsFrozen)
        {
            var text = new FormattedText("FROZEN · click to resume", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelTypeface, 10, CurrentBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(x, plot.Y + 4));
            x += text.Width + 10;
        }

        if (ScaleMode == YScaleMode.Manual)
        {
            var text = FormatText("MANUAL Y · wheel over each side", 10);
            dc.DrawText(text, new Point(x, plot.Y + 4));
        }
    }

    /// <summary>축 범위. Manual 이면 휠로 잡아둔 값, 아니면 Auto/Fit 계산값.</summary>
    private (double Min, double Max) ResolveAxis(
        MeasurementSample[] samples, Func<MeasurementSample, double> selector,
        double cap, double fallback, bool isVoltage)
        => ScaleMode == YScaleMode.Manual
            ? (isVoltage ? _manualVoltage : _manualCurrent)
            : ComputeAutoOrFit(samples, selector, cap, fallback);

    /// <summary>Auto 는 0부터 좋은 상한까지, Fit 은 데이터 최소~최대에 여유를 준다.</summary>
    private (double Min, double Max) ComputeAutoOrFit(
        MeasurementSample[] samples, Func<MeasurementSample, double> selector, double cap, double fallback)
    {
        if (samples.Length == 0) return (0, fallback);

        double peak = samples.Max(selector);
        if (ScaleMode != YScaleMode.Fit) return (0, NiceCeiling(peak, cap, fallback));

        double low = samples.Min(selector);
        double margin = Math.Max((peak - low) * 0.15, Math.Max(peak, 1e-3) * 0.002);
        return (Math.Max(0, low - margin), peak + margin);
    }

    private void DrawGrid(DrawingContext dc, Rect plot, (double Min, double Max) voltage, (double Min, double Max) current)
    {
        // 헤어라인은 물리 픽셀에 맞춰야 흐릿하게 번지지 않는다.
        var pen = new Pen(GridBrush, 1);
        double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        double voltageStep = (voltage.Max - voltage.Min) / GridLevels;
        double currentStep = (current.Max - current.Min) / GridLevels;

        for (int level = 0; level <= GridLevels; level++)
        {
            double ratio = (double)level / GridLevels;
            double y = Math.Round((plot.Bottom - ratio * plot.Height) * dpiScale) / dpiScale;

            dc.DrawLine(pen, new Point(plot.X, y), new Point(plot.Right, y));

            var leftLabel = FormatText(FormatTick(voltage.Min + (voltage.Max - voltage.Min) * ratio, voltageStep), 10);
            dc.DrawText(leftLabel, new Point(plot.X - 6 - leftLabel.Width, y - leftLabel.Height / 2));

            var rightLabel = FormatText(FormatTick(current.Min + (current.Max - current.Min) * ratio, currentStep), 10);
            dc.DrawText(rightLabel, new Point(plot.Right + 6, y - rightLabel.Height / 2));
        }
    }

    private static double XOf(Rect plot, DateTime t, DateTime asOf, TimeSpan span)
    {
        double age = (asOf - t).TotalMilliseconds;
        double ratio = 1 - Math.Clamp(age / span.TotalMilliseconds, 0, 1);
        return plot.X + plot.Width * ratio;
    }

    private static double YOf(Rect plot, double value, (double Min, double Max) axis)
    {
        double range = Math.Max(1e-9, axis.Max - axis.Min);
        return plot.Bottom - plot.Height * Math.Clamp((value - axis.Min) / range, 0, 1);
    }

    /// <summary>
    /// 점이 픽셀 열 수보다 많으면 열마다 최소/최대를 수직선으로, 적으면 폴리라인으로 그린다.
    /// 두 경로 모두 시간 간격이 벌어진 곳에서 선을 끊는다 — 없는 데이터를 이어 그리면 거짓이 된다.
    /// </summary>
    /// <summary>
    /// 공백 판정 기준. <b>저장 간격이 아니라 실제 샘플 간격</b>에서 뽑아야 한다 —
    /// 저장 간격은 하한일 뿐이라 폴링이 그보다 느리면 모든 구간이 공백으로 오판된다.
    /// </summary>
    private static TimeSpan ResolveGapThreshold(MeasurementWindow window)
    {
        var samples = window.Samples;
        if (samples.Length < 2) return window.StorageInterval * GapFactor;

        double averageMs = (samples[^1].Timestamp - samples[0].Timestamp).TotalMilliseconds / (samples.Length - 1);
        double floorMs = window.StorageInterval.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Max(averageMs, floorMs) * GapFactor);
    }

    private void DrawSeries(
        DrawingContext dc, Rect plot, MeasurementSample[] samples, DateTime asOf, TimeSpan span,
        TimeSpan gapThreshold, Func<MeasurementSample, double> selector,
        (double Min, double Max) axis, Brush brush)
    {
        var pen = new Pen(brush, 1.6) { LineJoin = PenLineJoin.Round };
        int columns = Math.Max(1, (int)plot.Width);

        if (samples.Length <= columns)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool open = false;
                for (int i = 0; i < samples.Length; i++)
                {
                    bool gap = i > 0 && samples[i].Timestamp - samples[i - 1].Timestamp > gapThreshold;
                    var point = new Point(XOf(plot, samples[i].Timestamp, asOf, span), YOf(plot, selector(samples[i]), axis));

                    if (!open || gap) { ctx.BeginFigure(point, isFilled: false, isClosed: false); open = true; }
                    else ctx.LineTo(point, isStroked: true, isSmoothJoin: true);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
            return;
        }

        var spine = new StreamGeometry();
        using (var ctx = spine.Open())
        {
            bool open = false;
            int index = 0;

            for (int column = 0; column < columns; column++)
            {
                double columnRight = plot.X + (column + 1) * plot.Width / columns;

                double min = double.MaxValue, max = double.MinValue;
                int taken = 0;
                while (index < samples.Length && XOf(plot, samples[index].Timestamp, asOf, span) <= columnRight)
                {
                    double value = selector(samples[index]);
                    min = Math.Min(min, value);
                    max = Math.Max(max, value);
                    taken++;
                    index++;
                }

                // 빈 열은 그대로 비워 공백이 보이게 하고, 다음 열에서 선을 새로 시작한다
                if (taken == 0) { open = false; continue; }

                double x = plot.X + (column + 0.5) * plot.Width / columns;
                double yMin = YOf(plot, min, axis), yMax = YOf(plot, max, axis);
                if (Math.Abs(yMin - yMax) > 0.5) dc.DrawLine(pen, new Point(x, yMin), new Point(x, yMax));

                var mid = new Point(x, (yMin + yMax) / 2);
                if (!open) { ctx.BeginFigure(mid, isFilled: false, isClosed: false); open = true; }
                else ctx.LineTo(mid, isStroked: true, isSmoothJoin: true);
            }
        }
        spine.Freeze();
        dc.DrawGeometry(null, pen, spine);
    }

    /// <summary>
    /// 플롯 아래 얇은 띠에 레귤레이션 상태를 시간순으로 칠한다.
    /// 언제 CC 였고 언제 트립했는지가 파형과 나란히 보인다.
    /// </summary>
    /// <summary>
    /// 상태를 <b>샘플 구간</b> 단위로 칠한다. 픽셀 열 단위로 하면 샘플이 열보다 드물 때
    /// 샘플 사이 열이 비어 점선처럼 끊긴다. 각 샘플의 상태는 다음 샘플까지 유효하다.
    /// </summary>
    private void DrawStateBand(
        DrawingContext dc, Rect plot, MeasurementSample[] samples, DateTime asOf, TimeSpan span, TimeSpan gapThreshold)
    {
        double y = plot.Bottom + BandGap;
        var typicalSpacing = gapThreshold / GapFactor;

        int i = 0;
        while (i < samples.Length)
        {
            var state = (samples[i].OutputEnabled, samples[i].Regulation);
            double left = XOf(plot, samples[i].Timestamp, asOf, span);

            // 같은 상태가 이어지는 동안 확장하고, 시간 공백에서 끊는다
            int j = i;
            while (j + 1 < samples.Length
                   && (samples[j + 1].OutputEnabled, samples[j + 1].Regulation) == state
                   && samples[j + 1].Timestamp - samples[j].Timestamp <= gapThreshold)
                j++;

            bool continues = j + 1 < samples.Length
                             && samples[j + 1].Timestamp - samples[j].Timestamp <= gapThreshold;

            double right = continues
                ? XOf(plot, samples[j + 1].Timestamp, asOf, span)
                : Math.Min(plot.Right, XOf(plot, samples[j].Timestamp + typicalSpacing, asOf, span));

            FillBandRun(dc, left, right, y, state);
            i = j + 1;
        }
    }

    private void FillBandRun(DrawingContext dc, double left, double right, double y, (bool Enabled, OutputRegulation Worst) state)
    {
        var fill = !state.Enabled ? GridBrush
            : state.Worst switch
            {
                OutputRegulation.OverCurrent or OutputRegulation.ConstantCurrent => CurrentBrush,
                _ => VoltageBrush,
            };

        double opacity = !state.Enabled ? 0.5 : state.Worst == OutputRegulation.ConstantVoltage ? 0.35 : 1.0;

        dc.PushOpacity(opacity);
        dc.DrawRectangle(fill, null, new Rect(left, y, Math.Max(1, right - left), BandHeight));
        dc.Pop();
    }

    private void DrawTimeLabels(DrawingContext dc, Rect plot, DateTime asOf, TimeSpan span)
    {
        // 창이 길면 초 단위는 의미가 없다
        string format = span >= TimeSpan.FromMinutes(10) ? "HH:mm" : "HH:mm:ss";
        double y = plot.Bottom + BandGap + BandHeight + 2;

        var start = FormatText((asOf - span).ToString(format, CultureInfo.InvariantCulture), 10);
        var end = FormatText(asOf.ToString(format, CultureInfo.InvariantCulture), 10);

        dc.DrawText(start, new Point(plot.X, y));
        dc.DrawText(end, new Point(plot.Right - end.Width, y));
    }

    /// <summary>커서 위치에 십자선과 가장 가까운 샘플의 값을 띄운다.</summary>
    private void DrawCursor(
        DrawingContext dc, Rect plot, MeasurementSample[] samples, DateTime asOf, TimeSpan span,
        double cursorX, (double Min, double Max) voltageAxis, (double Min, double Max) currentAxis)
    {
        double x = Math.Clamp(cursorX, plot.X, plot.Right);

        // x 를 시각으로 되돌려 가장 가까운 샘플을 찾는다
        double ratio = (x - plot.X) / plot.Width;
        var target = asOf - span + span * ratio;

        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < samples.Length; i++)
        {
            double distance = Math.Abs((samples[i].Timestamp - target).TotalMilliseconds);
            if (distance >= best) continue;
            best = distance;
            nearest = i;
        }

        var sample = samples[nearest];
        double sampleX = XOf(plot, sample.Timestamp, asOf, span);

        var guide = new Pen(AxisLabelBrush, 1) { DashStyle = new DashStyle([3, 3], 0) };
        dc.DrawLine(guide, new Point(sampleX, plot.Y), new Point(sampleX, plot.Bottom));

        double vy = YOf(plot, sample.Volts, voltageAxis);
        double ay = YOf(plot, sample.Amps, currentAxis);
        dc.DrawEllipse(VoltageBrush, null, new Point(sampleX, vy), 3, 3);
        dc.DrawEllipse(CurrentBrush, null, new Point(sampleX, ay), 3, 3);

        var lines = new[]
        {
            sample.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            $"{sample.Volts,7:F3} V",
            $"{sample.Amps,7:F3} A",
            $"{sample.Watts,7:F2} W",
            sample.OutputEnabled ? RegulationText(sample.Regulation) : "OFF",
        };

        var texts = lines.Select(l => FormatReadout(l)).ToArray();
        double boxWidth = texts.Max(t => t.Width) + 12;
        double boxHeight = texts.Sum(t => t.Height) + 10;

        // 커서 오른쪽에 두되 플롯을 벗어나면 왼쪽으로 뒤집는다
        double boxX = sampleX + 10;
        if (boxX + boxWidth > plot.Right) boxX = sampleX - 10 - boxWidth;
        double boxY = Math.Min(plot.Y + 4, plot.Bottom - boxHeight - 4);

        var box = new Rect(boxX, boxY, boxWidth, boxHeight);
        dc.DrawRectangle(ReadoutBackground, new Pen(GridBrush, 1), box);

        double textY = boxY + 5;
        foreach (var text in texts)
        {
            dc.DrawText(text, new Point(boxX + 6, textY));
            textY += text.Height;
        }
    }

    private static string RegulationText(OutputRegulation regulation) => regulation switch
    {
        OutputRegulation.ConstantCurrent => "CC",
        OutputRegulation.OverCurrent => "OC",
        _ => "CV",
    };

    private FormattedText FormatText(string text, double size) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, size,
        AxisLabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private FormattedText FormatReadout(string text) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 11,
        ReadoutForeground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

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

    /// <summary>
    /// 소수 자리는 <b>눈금 간격</b>에서 정한다. 최대값 기준으로 하면 Fit 모드처럼 범위가 좁을 때
    /// 눈금이 모두 같은 숫자(12 / 12 / 12…)로 뭉개진다.
    /// </summary>
    private static string FormatTick(double value, double step)
    {
        if (step <= 0) return value.ToString("F1", CultureInfo.InvariantCulture);

        int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(step)), 0, 4);

        // 간격이 1~2 사이면 정수 표기가 7 / 8 / 10 / 11 / 12 / 14 처럼 들쭉날쭉해진다
        if (decimals == 0 && step < 2) decimals = 1;

        return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}
