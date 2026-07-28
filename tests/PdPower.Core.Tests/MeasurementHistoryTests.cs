using PdPower.Core.Models;

namespace PdPower.Core.Tests;

public class MeasurementHistoryTests
{
    private static readonly DateTime T0 = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Local);

    private static MeasurementSample At(double seconds, double volts = 1, double amps = 0.1)
        => new(T0.AddSeconds(seconds), volts, amps);

    [Fact]
    public void 저장_간격은_창을_최대점수로_나눈_값이다()
    {
        var history = new MeasurementHistory(maxPoints: 240) { Window = TimeSpan.FromMinutes(1) };

        // 60초 / 240점 = 250 ms
        Assert.Equal(250, history.StorageInterval.TotalMilliseconds, 1);
    }

    [Fact]
    public void 창이_길어지면_저장_간격도_벌어진다()
    {
        var history = new MeasurementHistory(maxPoints: 14_400);

        history.Window = TimeSpan.FromMinutes(1);
        Assert.Equal(4.17, history.StorageInterval.TotalMilliseconds, 1);

        history.Window = TimeSpan.FromHours(1);
        Assert.Equal(250, history.StorageInterval.TotalMilliseconds, 1);
    }

    [Fact]
    public void 저장_간격보다_이른_샘플은_버린다()
    {
        // 창 60초 / 60점 = 1초 간격
        var history = new MeasurementHistory(maxPoints: 60) { Window = TimeSpan.FromMinutes(1) };

        Assert.True(history.Add(At(0)));    // 첫 점은 항상 저장
        Assert.False(history.Add(At(0.5))); // 0.5초 뒤 — 너무 이르다
        Assert.True(history.Add(At(1.0)));  // 1초 뒤 — 저장
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void 창을_벗어난_점은_스냅샷에서_빠진다()
    {
        var history = new MeasurementHistory(maxPoints: 1000) { Window = TimeSpan.FromSeconds(10) };
        for (int s = 0; s <= 30; s++) history.Add(At(s));

        // 30초 시점에서 최근 10초 = 20~30초, 11개
        var snapshot = history.Snapshot(T0.AddSeconds(30));

        Assert.Equal(11, snapshot.Length);
        Assert.Equal(T0.AddSeconds(20), snapshot[0].Timestamp);
        Assert.Equal(T0.AddSeconds(30), snapshot[^1].Timestamp);
    }

    [Fact]
    public void 스냅샷은_오래된_것부터_시간순이다()
    {
        // 저장 간격이 1초보다 짧아야 20개가 모두 담긴다 (600초 / 1000점 = 600 ms)
        var history = new MeasurementHistory(maxPoints: 1000) { Window = TimeSpan.FromMinutes(10) };
        for (int s = 0; s < 20; s++) history.Add(At(s, volts: s));

        var snapshot = history.Snapshot(T0.AddSeconds(20));

        Assert.Equal(20, snapshot.Length);
        for (int i = 0; i < snapshot.Length; i++) Assert.Equal(i, snapshot[i].Volts);
    }

    [Fact]
    public void 버퍼가_차면_가장_오래된_점을_덮어쓴다()
    {
        var history = new MeasurementHistory(maxPoints: 5) { Window = TimeSpan.FromHours(1) };

        // 창 3600초 / 5점 = 720초 간격이므로 그보다 벌려서 넣는다
        for (int i = 0; i < 8; i++) history.Add(At(i * 800, volts: i));

        Assert.Equal(5, history.Count);

        var snapshot = history.Snapshot(T0.AddSeconds(7 * 800));
        Assert.Equal(5, snapshot.Length);
        Assert.Equal(3, snapshot[0].Volts);   // 0~2 는 밀려났다
        Assert.Equal(7, snapshot[^1].Volts);
    }

    [Fact]
    public void 데이터가_없으면_빈_배열()
    {
        var history = new MeasurementHistory();
        Assert.Empty(history.Snapshot(T0));
    }

    [Fact]
    public void Clear_후에는_다음_샘플이_바로_저장된다()
    {
        var history = new MeasurementHistory(maxPoints: 60) { Window = TimeSpan.FromMinutes(1) };
        history.Add(At(0));
        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.True(history.Add(At(0.1)));   // 간격 제한이 초기화됐다
    }

    [Fact]
    public void Updated_는_저장된_경우에만_발생한다()
    {
        var history = new MeasurementHistory(maxPoints: 60) { Window = TimeSpan.FromMinutes(1) };
        int raised = 0;
        history.Updated += (_, _) => raised++;

        history.Add(At(0));     // 저장
        history.Add(At(0.5));   // 버림
        history.Add(At(1.0));   // 저장

        Assert.Equal(2, raised);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void 최대점수는_2_이상이어야_한다(int maxPoints)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MeasurementHistory(maxPoints));
    }

    [Fact]
    public void Version_은_저장된_경우에만_증가한다()
    {
        var history = new MeasurementHistory(maxPoints: 60) { Window = TimeSpan.FromMinutes(1) };
        long start = history.Version;

        history.Add(At(0));     // 저장
        history.Add(At(0.5));   // 버림
        history.Add(At(1.0));   // 저장

        Assert.Equal(start + 2, history.Version);
    }

    [Fact]
    public void Capture_는_창_정보까지_담는다()
    {
        var history = new MeasurementHistory(maxPoints: 240) { Window = TimeSpan.FromMinutes(1) };
        for (int i = 0; i < 5; i++) history.Add(At(i, volts: i));

        var window = history.Capture(T0.AddSeconds(5));

        Assert.Equal(5, window.Samples.Length);
        Assert.Equal(T0.AddSeconds(5), window.AsOf);
        Assert.Equal(TimeSpan.FromMinutes(1), window.Window);
        Assert.Equal(250, window.StorageInterval.TotalMilliseconds, 1);
        Assert.False(window.IsEmpty);
    }

    [Fact]
    public void Capture_한_장은_이후_추가에_영향받지_않는다()
    {
        var history = new MeasurementHistory(maxPoints: 240) { Window = TimeSpan.FromMinutes(1) };
        history.Add(At(0));

        var frozen = history.Capture(T0);
        history.Add(At(1));

        // 정지 화면이 뒤에서 들어온 데이터로 흔들리면 안 된다
        Assert.Single(frozen.Samples);
    }

    [Fact]
    public void 통계는_창_안의_최소_평균_최대를_준다()
    {
        var history = new MeasurementHistory(maxPoints: 240) { Window = TimeSpan.FromMinutes(1) };
        history.Add(new MeasurementSample(T0, 10, 1.0));
        history.Add(new MeasurementSample(T0.AddSeconds(1), 12, 2.0));
        history.Add(new MeasurementSample(T0.AddSeconds(2), 14, 3.0));

        var stats = MeasurementStats.From(history.Capture(T0.AddSeconds(2)));

        Assert.Equal(10, stats.Volts.Min);
        Assert.Equal(12, stats.Volts.Avg);
        Assert.Equal(14, stats.Volts.Max);
        Assert.Equal(1.0, stats.Amps.Min);
        Assert.Equal(3.0, stats.Amps.Max);

        // 전력은 샘플별 V×A 의 통계여야 한다. (10 + 24 + 42) / 3 = 25.333 —
        // 평균전압 × 평균전류 = 12 × 2 = 24 가 아니다.
        Assert.Equal(10, stats.Watts.Min);
        Assert.Equal(42, stats.Watts.Max);
        Assert.Equal(25.333, stats.Watts.Avg, 3);
    }

    [Fact]
    public void 빈_창의_통계는_전부_0()
    {
        var stats = MeasurementStats.From(MeasurementWindow.Empty);
        Assert.Equal(0, stats.Volts.Max);
        Assert.Equal(0, stats.Watts.Avg);
    }
}
