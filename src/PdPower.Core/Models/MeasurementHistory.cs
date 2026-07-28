namespace PdPower.Core.Models;

/// <summary>
/// Trend 차트 한 점. 레귤레이션 상태를 같이 담아 차트 아래 상태 띠를 그린다 —
/// 상태는 측정보다 드물게 읽으므로 마지막으로 알려진 값이 들어간다.
/// </summary>
public readonly record struct MeasurementSample(
    DateTime Timestamp,
    double Volts,
    double Amps,
    OutputRegulation Regulation = OutputRegulation.ConstantVoltage,
    bool OutputEnabled = false)
{
    public double Watts => Volts * Amps;
}

/// <summary>
/// 특정 시점에 잘라낸 이력 한 장. 정지(freeze) 하면 이 객체를 붙잡아 두므로
/// 뒤에서 링 버퍼가 덮여도 화면은 안정적으로 유지된다.
/// </summary>
public sealed record MeasurementWindow(
    MeasurementSample[] Samples,
    DateTime AsOf,
    TimeSpan Window,
    TimeSpan StorageInterval)
{
    public static MeasurementWindow Empty { get; } =
        new([], DateTime.MinValue, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(250));

    public bool IsEmpty => Samples.Length == 0;
}

/// <summary>한 시리즈의 창 통계.</summary>
public readonly record struct SeriesStats(double Min, double Avg, double Max);

/// <summary>보이는 구간의 전압·전류·전력 통계.</summary>
public readonly record struct MeasurementStats(SeriesStats Volts, SeriesStats Amps, SeriesStats Watts)
{
    public static MeasurementStats From(MeasurementWindow window)
    {
        if (window.IsEmpty) return default;

        double vMin = double.MaxValue, vMax = double.MinValue, vSum = 0;
        double aMin = double.MaxValue, aMax = double.MinValue, aSum = 0;
        double wMin = double.MaxValue, wMax = double.MinValue, wSum = 0;

        foreach (var s in window.Samples)
        {
            vMin = Math.Min(vMin, s.Volts); vMax = Math.Max(vMax, s.Volts); vSum += s.Volts;
            aMin = Math.Min(aMin, s.Amps); aMax = Math.Max(aMax, s.Amps); aSum += s.Amps;
            double w = s.Watts;
            wMin = Math.Min(wMin, w); wMax = Math.Max(wMax, w); wSum += w;
        }

        int n = window.Samples.Length;
        return new MeasurementStats(
            new SeriesStats(vMin, vSum / n, vMax),
            new SeriesStats(aMin, aSum / n, aMax),
            new SeriesStats(wMin, wSum / n, wMax));
    }
}

/// <summary>
/// 시간 기준 측정 이력. 링 버퍼라 할당이 한 번뿐이고, 저장 간격을 창 길이에서 유도해
/// 점 개수가 <see cref="MaxPoints"/> 를 넘지 않는다.
/// </summary>
/// <remarks>
/// 폴링 주기와 저장 주기를 분리하는 것이 요점이다. 10 ms 로 폴링하면서 1시간 창을 그리려면
/// 36만 점이 필요한데, 그건 메모리도 렌더링도 감당할 수 없다. 창이 길수록 드물게 저장한다.
/// 백그라운드 폴링 스레드가 <see cref="Add"/>, UI 스레드가 <see cref="Snapshot"/> 를 호출하므로
/// 모든 접근을 락으로 보호한다.
/// </remarks>
public sealed class MeasurementHistory
{
    /// <summary>1시간 창을 250 ms 간격으로 담는 크기 — 그 이상은 화면에 그릴 수도 없다.</summary>
    public const int DefaultMaxPoints = 14_400;

    private readonly object _gate = new();
    private readonly MeasurementSample[] _buffer;
    private int _count;
    private int _next;
    private DateTime _lastStored = DateTime.MinValue;
    private TimeSpan _window = TimeSpan.FromMinutes(1);
    private long _version;

    public MeasurementHistory(int maxPoints = DefaultMaxPoints)
    {
        if (maxPoints < 2) throw new ArgumentOutOfRangeException(nameof(maxPoints), maxPoints, "2 이상이어야 합니다.");
        _buffer = new MeasurementSample[maxPoints];
    }

    public int MaxPoints => _buffer.Length;

    /// <summary>표시 구간. 길게 잡으면 저장 간격도 그만큼 벌어진다.</summary>
    public TimeSpan Window
    {
        get { lock (_gate) return _window; }
        set
        {
            if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value), value, "양수여야 합니다.");
            lock (_gate) _window = value;
        }
    }

    /// <summary>
    /// 이 간격보다 촘촘하게 들어온 샘플은 버린다. 창을 <see cref="MaxPoints"/> 로 나눈 값이므로
    /// 창이 짧으면 폴링 주기 그대로, 창이 길면 알아서 드물게 저장된다.
    /// </summary>
    public TimeSpan StorageInterval
    {
        get { lock (_gate) return TimeSpan.FromMilliseconds(Math.Max(1, _window.TotalMilliseconds / _buffer.Length)); }
    }

    public int Count { get { lock (_gate) return _count; } }

    /// <summary>
    /// 내용이 바뀔 때마다 증가. 화면이 매 프레임 스냅샷을 새로 뜨지 않도록
    /// 값이 그대로면 건너뛰는 데 쓴다.
    /// </summary>
    public long Version { get { lock (_gate) return _version; } }

    /// <summary>새 점이 저장됐을 때. <b>호출한 스레드에서 발생</b>하므로 UI 갱신은 마샬링해야 한다.</summary>
    public event EventHandler? Updated;

    /// <summary>저장 간격을 만족하면 담고 <c>true</c>. 너무 이르면 버리고 <c>false</c>.</summary>
    public bool Add(MeasurementSample sample)
    {
        lock (_gate)
        {
            var minGap = TimeSpan.FromMilliseconds(Math.Max(1, _window.TotalMilliseconds / _buffer.Length));
            if (_count > 0 && sample.Timestamp - _lastStored < minGap) return false;

            _buffer[_next] = sample;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
            _lastStored = sample.Timestamp;
            _version++;
        }

        Updated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>창 안의 점들과 그 시점 정보를 한 장으로 잘라낸다.</summary>
    public MeasurementWindow Capture(DateTime asOf)
    {
        lock (_gate)
        {
            var interval = TimeSpan.FromMilliseconds(Math.Max(1, _window.TotalMilliseconds / _buffer.Length));
            return new MeasurementWindow(SnapshotCore(asOf), asOf, _window, interval);
        }
    }

    /// <summary>창 안에 드는 점만 오래된 것부터 돌려준다.</summary>
    public MeasurementSample[] Snapshot(DateTime asOf)
    {
        lock (_gate) return SnapshotCore(asOf);
    }

    /// <summary>락을 이미 잡은 상태에서 호출한다.</summary>
    private MeasurementSample[] SnapshotCore(DateTime asOf)
    {
        if (_count == 0) return [];

        var cutoff = asOf - _window;
        int start = (_next - _count + _buffer.Length) % _buffer.Length;

        // 오래된 쪽부터 훑으며 창에 처음 드는 지점을 찾는다 (버퍼는 시간순이다)
        int skip = 0;
        while (skip < _count && _buffer[(start + skip) % _buffer.Length].Timestamp < cutoff) skip++;

        var result = new MeasurementSample[_count - skip];
        for (int i = 0; i < result.Length; i++)
            result[i] = _buffer[(start + skip + i) % _buffer.Length];

        return result;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _count = 0;
            _next = 0;
            _lastStored = DateTime.MinValue;
            _version++;
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }
}
