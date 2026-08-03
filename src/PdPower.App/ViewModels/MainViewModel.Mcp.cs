using PdPower.Core;
using PdPower.Mcp;

namespace PdPower.App.ViewModels;

/// <summary>
/// MCP(AI 제어) 게이트웨이 구현과 서버 수명주기.
/// </summary>
/// <remarks>
/// COM 포트는 이 프로세스가 쥐고 있으므로 MCP 서버도 여기서 돈다.
/// 게이트웨이 메서드는 Kestrel 스레드에서 불리므로 — 읽기는 UI 스레드로 마샬링해 발행된
/// 값을 스냅샷하고, 제어는 기존 VM 경로를 그대로 태워 화면 상태까지 함께 갱신한다.
/// </remarks>
public sealed partial class MainViewModel : IPdPowerGateway
{
    private McpServerHost? _mcpHost;

    private bool _isMcpEnabled;
    public bool IsMcpEnabled
    {
        get => _isMcpEnabled;
        private set { if (SetField(ref _isMcpEnabled, value)) RaiseCommandStates(); }
    }

    /// <summary>Setup 화면에 보여줄 접속 주소. 서버가 꺼져 있어도 안내용으로 고정 표시한다.</summary>
    public string McpEndpoint { get; } = $"http://localhost:{McpServerHost.DefaultPort}";

    /// <summary>Claude Code 에 이 서버를 등록하는 한 줄 — Copy 버튼이 그대로 복사한다.</summary>
    public string McpRegisterCommand => $"claude mcp add --transport http pdpower {McpEndpoint}";

    public AsyncRelayCommand McpOnCommand { get; }
    public AsyncRelayCommand McpOffCommand { get; }
    public RelayCommand CopyMcpRegisterCommand { get; }

    private void CopyMcpRegister()
    {
        try
        {
            System.Windows.Clipboard.SetText(McpRegisterCommand);
            StatusMessage = "MCP 등록 명령을 복사했습니다 — Claude Code 터미널에 붙여넣으세요.";
        }
        catch (Exception ex)
        {
            // 다른 프로세스가 클립보드를 잡고 있으면 COMException 이 난다
            StatusMessage = $"클립보드 복사 실패: {ex.Message}";
        }
    }

    private async Task SetMcpEnabledAsync(bool enabled)
    {
        if (enabled == IsMcpEnabled) return;

        if (enabled)
        {
            try
            {
                _mcpHost = await McpServerHost.StartAsync(this, AppVersion).ConfigureAwait(true);
                IsMcpEnabled = true;
                AppendLog($"[MCP] 서버 시작 — {_mcpHost.Endpoint}");
                StatusMessage = $"MCP 서버 대기 중 — {_mcpHost.Endpoint}";
            }
            catch (Exception ex)
            {
                _mcpHost = null;
                AppendLog($"[MCP] 시작 실패 — {ex.Message}");
                StatusMessage = $"MCP 서버를 시작하지 못했습니다: {ex.Message}";
            }
        }
        else
        {
            var host = _mcpHost;
            _mcpHost = null;
            IsMcpEnabled = false;
            if (host is not null) await host.DisposeAsync().ConfigureAwait(true);
            AppendLog("[MCP] 서버 중지");
            StatusMessage = "MCP 서버를 중지했습니다.";
        }
    }

    // ── 게이트웨이: 읽기 ─────────────────────────────────────────────────

    async Task<McpStatus> IPdPowerGateway.GetStatusAsync(CancellationToken ct)
        => await _dispatcher.InvokeAsync(() => new McpStatus(
            IsConnected, IsReconnecting,
            IsConnected ? DeviceName : null,
            IsConnected ? FirmwareVersion : null,
            IsConnected ? SerialNumber : null,
            OutputEnabled, RegulationLabel,
            Math.Round(MeasuredVolts, 3), Math.Round(MeasuredAmps, 3), Math.Round(MeasuredWatts, 3),
            InputState, Math.Round(InputVolts, 3),
            ActivePresetId, SetVolts, SetAmps));

    /// <remarks>
    /// VM 캐시가 아니라 장치에서 되읽는다 — 사용자가 장치 노브로 바꾼 값도 이 시점에 반영된다.
    /// </remarks>
    async Task<McpSettings> IPdPowerGateway.GetSettingsAsync(CancellationToken ct)
    {
        var device = _device;
        if (device is null)
            return await _dispatcher.InvokeAsync(() =>
                new McpSettings(false, [], 0, false, 0, 0, PollIntervalMs, HasUnsavedChanges));

        int active = await device.ReadActivePresetIdAsync(ct).ConfigureAwait(false);
        var presets = new McpPreset[PdPowerDevice.PresetCount];
        for (int id = 0; id < presets.Length; id++)
        {
            var preset = await device.ReadPresetAsync(id, ct).ConfigureAwait(false);
            presets[id] = new McpPreset(id, preset.Volts, preset.Amps, id == active);
        }

        bool ocp = await device.ReadOcpEnabledAsync(ct).ConfigureAwait(false);
        int brightness = await device.ReadBrightnessAsync(ct).ConfigureAwait(false);

        double requestedPd = 0;
        try
        {
            requestedPd = (await device.ReadInputStatusAsync(ct).ConfigureAwait(false)).RequestedPdVolts;
        }
        catch (PdPowerException)
        {
            // INPUT_STATE 는 펌웨어 v1.0.2.0 이상 — 폴링 루프와 같은 처리
        }

        return await _dispatcher.InvokeAsync(() =>
            new McpSettings(IsConnected, presets, active, ocp, brightness, requestedPd,
                            PollIntervalMs, HasUnsavedChanges));
    }

    async Task<McpHistoryStats> IPdPowerGateway.GetHistoryStatsAsync(CancellationToken ct)
        => await _dispatcher.InvokeAsync(() => new McpHistoryStats(
            SampleCount, SelectedRangeSeconds, IsFrozen,
            ToSeries(Stats.Volts), ToSeries(Stats.Amps), ToSeries(Stats.Watts)));

    private static McpSeries ToSeries(Core.Models.SeriesStats s)
        => new(Math.Round(s.Min, 3), Math.Round(s.Avg, 3), Math.Round(s.Max, 3));

    // ── 게이트웨이: 제어 ─────────────────────────────────────────────────

    Task<string> IPdPowerGateway.SetOutputAsync(bool enabled, CancellationToken ct)
        => McpInvokeAsync($"set_output {(enabled ? "on" : "off")}", async () =>
        {
            await SetOutputAsync(enabled).ConfigureAwait(true);
            return enabled ? "출력을 켰습니다." : "출력을 껐습니다.";
        });

    Task<string> IPdPowerGateway.SetSetpointAsync(double? volts, double? amps, CancellationToken ct)
        => McpInvokeAsync($"set_setpoint {volts?.ToString("F3") ?? "-"}V {amps?.ToString("F3") ?? "-"}A", async () =>
        {
            if (volts is null && amps is null)
                throw new InvalidOperationException("volts 또는 amps 중 하나는 지정해야 합니다.");

            await ApplySetpointAsync(volts ?? SetVolts, amps ?? SetAmps).ConfigureAwait(true);
            return $"M{ActivePresetId} → {SetVolts:F3} V / {SetAmps:F3} A. 휘발성 — 유지하려면 save_config.";
        });

    Task<string> IPdPowerGateway.SelectPresetAsync(int presetId, CancellationToken ct)
        => McpInvokeAsync($"select_preset M{presetId}", async () =>
        {
            // GUI 와 같은 안전 규칙 — 출력 중 프리셋 전환은 부하에 갑작스런 전압 변화를 준다
            if (OutputEnabled)
                throw new InvalidOperationException("출력이 켜져 있는 동안에는 프리셋을 전환할 수 없습니다. 먼저 set_output false 를 실행하세요.");

            await SelectPresetAsync(presetId).ConfigureAwait(true);
            return $"M{presetId} 적용 — {SetVolts:F3} V / {SetAmps:F3} A";
        });

    Task<string> IPdPowerGateway.SetOcpAsync(bool enabled, CancellationToken ct)
        => McpInvokeAsync($"set_ocp {(enabled ? "on" : "off")}", async () =>
        {
            await SetOcpAsync(enabled).ConfigureAwait(true);
            return IsOcpEnabled
                ? $"OCP 켜짐 — {SetAmps:F3} A 초과 시 약 200 ms 후 출력 차단."
                : "OCP 꺼짐 — 전류 제한 도달 시 CC 동작으로 출력 유지.";
        });

    Task<string> IPdPowerGateway.SetPdVoltageAsync(int volts, CancellationToken ct)
        => McpInvokeAsync($"set_pd_voltage {volts}V", async () =>
        {
            if (!PdVoltageOptions.Contains(volts))
                throw new InvalidOperationException($"PD 요청 전압은 {string.Join("/", PdVoltageOptions)} V 만 가능합니다.");
            if (OutputEnabled)
                throw new InvalidOperationException("출력이 켜져 있는 동안에는 PD 전압을 바꿀 수 없습니다. 먼저 set_output false 를 실행하세요.");

            SelectedPdVoltage = volts;
            await SetPdVoltageAsync().ConfigureAwait(true);
            return $"PD 입력 {volts} V 요청 — 출력 OFF & 출력전압 5 V 미만일 때만 반영됩니다.";
        });

    Task<string> IPdPowerGateway.SetBrightnessAsync(int percent, CancellationToken ct)
        => McpInvokeAsync($"set_brightness {percent}%", async () =>
        {
            if (percent is < MinBrightness or > MaxBrightness)
                throw new InvalidOperationException($"밝기는 {MinBrightness}–{MaxBrightness} 범위여야 합니다.");

            // 슬라이더 디바운스를 거치지 않고 즉시 쓴다 — 직후 save_config 와 경합하지 않도록
            _brightnessDebounce.Stop();
            await _device!.SetBrightnessAsync(percent).ConfigureAwait(true);
            await LoadBrightnessAsync().ConfigureAwait(true);
            HasUnsavedChanges = true;
            StatusMessage = $"LCD 밝기 {BrightnessPercent}%";
            return $"LCD 밝기 {BrightnessPercent}%. 휘발성 — 유지하려면 save_config.";
        });

    Task<string> IPdPowerGateway.SaveConfigAsync(CancellationToken ct)
        => McpInvokeAsync("save_config", async () =>
        {
            await SaveConfigAsync().ConfigureAwait(true);
            return "현재 설정을 장치 플래시에 저장했습니다.";
        });

    /// <summary>
    /// 제어 요청을 UI 스레드에서 기존 VM 경로로 실행한다 — 화면·UNSAVED 상태가 함께 갱신되고,
    /// AI 가 무엇을 했는지 Log 에 [MCP] 로 남는다.
    /// </summary>
    private async Task<string> McpInvokeAsync(string what, Func<Task<string>> action)
    {
        AppendLog($"[MCP] {what}");
        return await await _dispatcher.InvokeAsync(async () =>
        {
            if (_device is null)
                throw new InvalidOperationException("장치가 연결되어 있지 않습니다 — GUI에서 포트를 먼저 연결하세요.");
            return await action().ConfigureAwait(true);
        });
    }
}
