using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace PdPower.Mcp;

/// <summary>
/// AI 클라이언트에 노출되는 도구 모음. 실제 동작은 전부 <see cref="IPdPowerGateway"/> 로 위임한다.
/// </summary>
/// <remarks>
/// 게이트웨이가 던지는 예외는 <see cref="McpException"/> 으로 변환해야 클라이언트가
/// 메시지를 그대로 본다 — 일반 예외는 SDK 가 내용을 가린다.
/// </remarks>
[McpServerToolType]
public sealed class PdPowerMcpTools(IPdPowerGateway gateway)
{
    [McpServerTool(Name = "get_status")]
    [Description("전원 장치의 현재 상태를 읽는다: 연결 여부, 출력 on/off, CV/CC/OC 판정, 실측 전압(V)/전류(A)/전력(W), 입력(PD) 상태, 활성 프리셋과 설정값.")]
    public Task<McpStatus> GetStatus(CancellationToken ct)
        => Guard(() => gateway.GetStatusAsync(ct));

    [McpServerTool(Name = "get_settings")]
    [Description("장치 설정을 읽는다: 프리셋 M0–M4 의 전압/전류, OCP 여부, LCD 밝기, PD 요청 전압, 앱 폴링 주기, 미저장 변경 여부. 장치 노브로 바꾼 값도 되읽어 반영된다.")]
    public Task<McpSettings> GetSettings(CancellationToken ct)
        => Guard(() => gateway.GetSettingsAsync(ct));

    [McpServerTool(Name = "get_history_stats")]
    [Description("Trend 그래프에 보이는 구간의 측정 통계를 읽는다: 샘플 수, 시간 범위, 전압/전류/전력 각각의 min/avg/max.")]
    public Task<McpHistoryStats> GetHistoryStats(CancellationToken ct)
        => Guard(() => gateway.GetHistoryStatsAsync(ct));

    [McpServerTool(Name = "set_output")]
    [Description("출력을 켜거나 끈다. 켜면 실제 부하에 전력이 공급되므로 전압/전류 설정을 먼저 확인할 것.")]
    public Task<string> SetOutput(
        [Description("true = 출력 ON, false = 출력 OFF")] bool enabled,
        CancellationToken ct)
        => Guard(() => gateway.SetOutputAsync(enabled, ct));

    [McpServerTool(Name = "set_setpoint")]
    [Description("활성 프리셋의 출력 전압/전류 제한을 바꾼다. 전압 1–20 V, 전류 0–3 A. 출력이 켜진 상태에서도 즉시 반영된다. 저장(save_config) 전까지는 휘발성.")]
    public Task<string> SetSetpoint(
        [Description("목표 전압(V). 생략하면 현재 값 유지.")] double? volts = null,
        [Description("전류 제한(A). 생략하면 현재 값 유지.")] double? amps = null,
        CancellationToken ct = default)
        => Guard(() => gateway.SetSetpointAsync(volts, amps, ct));

    [McpServerTool(Name = "select_preset")]
    [Description("활성 프리셋을 전환한다(M0–M4). 안전을 위해 출력이 꺼져 있을 때만 허용된다.")]
    public Task<string> SelectPreset(
        [Description("프리셋 번호 0–4")] int presetId,
        CancellationToken ct)
        => Guard(() => gateway.SelectPresetAsync(presetId, ct));

    [McpServerTool(Name = "set_ocp")]
    [Description("과전류 보호(OCP)를 켜거나 끈다. 임계값은 별도 설정이 아니라 현재 전류 제한값이며, 초과 후 약 200 ms 뒤 출력이 차단된다. 끄면 CC 동작으로 출력이 유지된다.")]
    public Task<string> SetOcp(
        [Description("true = OCP ON, false = OCP OFF")] bool enabled,
        CancellationToken ct)
        => Guard(() => gateway.SetOcpAsync(enabled, ct));

    [McpServerTool(Name = "set_pd_voltage")]
    [Description("PD 어댑터에 요청할 입력 전압을 바꾼다. 9/12/15/20 V 만 유효. 장치는 출력이 OFF 이고 출력 전압이 5 V 미만일 때만 반영한다.")]
    public Task<string> SetPdVoltage(
        [Description("요청 전압(V): 9, 12, 15, 20 중 하나")] int volts,
        CancellationToken ct)
        => Guard(() => gateway.SetPdVoltageAsync(volts, ct));

    [McpServerTool(Name = "set_brightness")]
    [Description("장치 LCD 밝기를 바꾼다(1–100 %).")]
    public Task<string> SetBrightness(
        [Description("밝기 퍼센트 1–100")] int percent,
        CancellationToken ct)
        => Guard(() => gateway.SetBrightnessAsync(percent, ct));

    [McpServerTool(Name = "save_config")]
    [Description("현재 설정(프리셋, OCP, 오프셋, 밝기, PD 전압)을 장치 플래시에 저장한다. 저장하지 않으면 전원 재인가 시 이전 값으로 돌아간다.")]
    public Task<string> SaveConfig(CancellationToken ct)
        => Guard(() => gateway.SaveConfigAsync(ct));

    private static async Task<T> Guard<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (McpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
