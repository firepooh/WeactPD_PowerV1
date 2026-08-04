namespace PdPower.Core.Models;

/// <summary>
/// 누적 전력량(Wh) 적분기 — Wh += V × A × Δt(h). 명목 주기가 아니라 실제 경과로 적분한다.
/// </summary>
/// <remarks>
/// 스레드 계약: <see cref="Add"/>/<see cref="BreakSpan"/>/<see cref="ResetNow"/> 는
/// 폴링 스레드 전용, <see cref="RequestReset"/> 와 <see cref="WattHours"/> 읽기는
/// 어느 스레드든 가능하다 (리셋 요청은 다음 샘플에서 반영된다).
/// </remarks>
public sealed class EnergyMeter
{
    private double _wattHours;
    private DateTime? _lastSample;
    private volatile bool _resetRequested;

    public double WattHours => _wattHours;

    /// <summary>다음 샘플에서 0으로 — UI 스레드에서 안전하게 부를 수 있다.</summary>
    public void RequestReset() => _resetRequested = true;

    /// <summary>즉시 0으로 — 폴링이 멈춰 있을 때만 직접 부른다.</summary>
    public void ResetNow()
    {
        _wattHours = 0;
        _lastSample = null;
        _resetRequested = false;
    }

    /// <summary>적분 구간을 끊는다 — 재접속 등으로 끊긴 공백을 적분하지 않기 위해.</summary>
    public void BreakSpan() => _lastSample = null;

    /// <summary>샘플 하나를 반영하고 누적값을 돌려준다.</summary>
    public double Add(DateTime timestamp, double volts, double amps)
    {
        if (_resetRequested) ResetNow();

        if (_lastSample is { } last && timestamp > last)
            _wattHours += volts * amps * (timestamp - last).TotalHours;

        _lastSample = timestamp;
        return _wattHours;
    }

    /// <summary>표시 규칙(목업): 10 미만 3자리, 100 미만 2자리, 이상 1자리.</summary>
    public static string Format(double wattHours) =>
        wattHours < 10 ? $"{wattHours:F3}"
        : wattHours < 100 ? $"{wattHours:F2}"
        : $"{wattHours:F1}";
}
