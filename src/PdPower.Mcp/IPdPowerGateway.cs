namespace PdPower.Mcp;

/// <summary>
/// MCP 도구가 앱을 조작할 때 쓰는 통로. GUI(MainViewModel)가 구현한다.
/// </summary>
/// <remarks>
/// Kestrel 스레드에서 호출되므로 구현체가 UI 스레드 마샬링을 책임진다.
/// 제어 메서드는 실패 시 예외를 던진다 — 도구 계층이 MCP 오류로 변환한다.
/// </remarks>
public interface IPdPowerGateway
{
    Task<McpStatus> GetStatusAsync(CancellationToken ct);

    Task<McpSettings> GetSettingsAsync(CancellationToken ct);

    Task<McpHistoryStats> GetHistoryStatsAsync(CancellationToken ct);

    /// <returns>수행 결과를 설명하는 한 줄 — AI 가 그대로 읽는다.</returns>
    Task<string> SetOutputAsync(bool enabled, CancellationToken ct);

    /// <param name="volts">null 이면 현재 값 유지.</param>
    /// <param name="amps">null 이면 현재 값 유지.</param>
    Task<string> SetSetpointAsync(double? volts, double? amps, CancellationToken ct);

    Task<string> SelectPresetAsync(int presetId, CancellationToken ct);

    Task<string> SetOcpAsync(bool enabled, CancellationToken ct);

    Task<string> SetPdVoltageAsync(int volts, CancellationToken ct);

    Task<string> SetBrightnessAsync(int percent, CancellationToken ct);

    Task<string> SaveConfigAsync(CancellationToken ct);
}

/// <summary>get_status 응답. 실측값은 폴링 루프의 최신 발행값(≤60 ms 지연)이다.</summary>
public sealed record McpStatus(
    bool Connected,
    bool ReconnectWaiting,
    string? DeviceName,
    string? FirmwareVersion,
    string? SerialNumber,
    bool OutputEnabled,
    string Regulation,
    double MeasuredVolts,
    double MeasuredAmps,
    double MeasuredWatts,
    double EnergyWh,
    string InputState,
    double InputVolts,
    int ActivePresetId,
    double SetVolts,
    double SetAmps);

public sealed record McpPreset(int Id, double Volts, double Amps, bool Active);

/// <summary>get_settings 응답. OCP·밝기·프리셋은 장치에서 즉석으로 되읽는다 — 노브 변경도 반영된다.</summary>
public sealed record McpSettings(
    bool Connected,
    McpPreset[] Presets,
    int ActivePresetId,
    bool OcpEnabled,
    int BrightnessPercent,
    double RequestedPdVolts,
    int PollIntervalMs,
    bool HasUnsavedChanges);

public sealed record McpSeries(double Min, double Avg, double Max);

/// <summary>get_history_stats 응답 — Trend 창에 보이는 구간의 통계.</summary>
public sealed record McpHistoryStats(
    int SampleCount,
    int RangeSeconds,
    bool Frozen,
    McpSeries Volts,
    McpSeries Amps,
    McpSeries Watts);
