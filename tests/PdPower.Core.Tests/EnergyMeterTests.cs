using PdPower.Core.Models;
using Xunit;

namespace PdPower.Core.Tests;

public class EnergyMeterTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void FirstSampleAccumulatesNothing()
    {
        var meter = new EnergyMeter();
        Assert.Equal(0, meter.Add(T0, 12.0, 1.0));
    }

    [Fact]
    public void IntegratesVoltsTimesAmpsOverElapsedHours()
    {
        var meter = new EnergyMeter();
        meter.Add(T0, 12.0, 1.0);
        // 12 W × 30분 = 6 Wh
        double wh = meter.Add(T0.AddMinutes(30), 12.0, 1.0);
        Assert.Equal(6.0, wh, precision: 9);
    }

    [Fact]
    public void UsesActualElapsedTimeNotNominalInterval()
    {
        var meter = new EnergyMeter();
        meter.Add(T0, 10.0, 2.0);
        meter.Add(T0.AddSeconds(1), 10.0, 2.0);     // 20 W × 1 s
        meter.Add(T0.AddSeconds(4), 10.0, 2.0);     // 20 W × 3 s (지연된 틱)
        Assert.Equal(20.0 * 4 / 3600, meter.WattHours, precision: 9);
    }

    [Fact]
    public void BreakSpanSkipsTheGap()
    {
        var meter = new EnergyMeter();
        meter.Add(T0, 12.0, 1.0);
        meter.Add(T0.AddHours(1), 12.0, 1.0);       // 12 Wh
        meter.BreakSpan();                          // 재접속 공백
        meter.Add(T0.AddHours(5), 12.0, 1.0);       // 공백 4시간은 적분 안 됨
        double before = meter.WattHours;
        Assert.Equal(12.0, before, precision: 9);
        meter.Add(T0.AddHours(6), 12.0, 1.0);       // 다시 1시간 = +12
        Assert.Equal(24.0, meter.WattHours, precision: 9);
    }

    [Fact]
    public void RequestResetAppliesOnNextSample()
    {
        var meter = new EnergyMeter();
        meter.Add(T0, 12.0, 1.0);
        meter.Add(T0.AddHours(1), 12.0, 1.0);
        meter.RequestReset();
        Assert.Equal(12.0, meter.WattHours, precision: 9);   // 아직 그대로
        meter.Add(T0.AddHours(2), 12.0, 1.0);                // 리셋 후 첫 샘플 — 0부터
        Assert.Equal(0.0, meter.WattHours, precision: 9);
        meter.Add(T0.AddHours(3), 12.0, 1.0);
        Assert.Equal(12.0, meter.WattHours, precision: 9);
    }

    [Fact]
    public void OutOfOrderTimestampIsIgnored()
    {
        var meter = new EnergyMeter();
        meter.Add(T0, 12.0, 1.0);
        meter.Add(T0.AddSeconds(-5), 12.0, 1.0);    // 시계 역행 — 음수 적분 금지
        Assert.Equal(0.0, meter.WattHours, precision: 9);
    }

    [Theory]
    [InlineData(0.0, "0.000")]
    [InlineData(0.0234, "0.023")]
    [InlineData(9.9994, "9.999")]
    [InlineData(10.0, "10.00")]
    [InlineData(99.994, "99.99")]
    [InlineData(100.0, "100.0")]
    [InlineData(1234.56, "1234.6")]
    public void FormatShrinksDigitsAsValueGrows(double wh, string expected)
        => Assert.Equal(expected, EnergyMeter.Format(wh));
}
