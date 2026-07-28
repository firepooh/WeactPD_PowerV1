using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using PdPower.Core;
using PdPower.Core.Models;
using PdPower.Core.Protocol;

namespace PdPower.App.ViewModels;

/// <summary>
/// Monitor 화면 상태. 250 ms 폴링으로 실측값을 갱신하고 설정 변경을 장치에 즉시 반영한다.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Trend 차트가 보관하는 샘플 수.</summary>
    public const int HistoryCapacity = 64;

    /// <summary>Log 탭이 보관하는 줄 수.</summary>
    public const int LogCapacity = 500;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>슬라이더 드래그가 멈춘 뒤 실제로 밝기를 쓰기까지 기다리는 시간.</summary>
    private static readonly TimeSpan BrightnessWriteDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>재접속 시도 간격.</summary>
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(1);

    /// <summary>이 횟수만큼 실패하면 재접속을 포기한다 (간격 × 횟수 = 대기 시간).</summary>
    public const int MaxReconnectAttempts = 60;

    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _brightnessDebounce;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private PdPowerDevice? _device;
    private bool _polling;
    private bool _suppressBrightnessWrite;

    // 재접속 중 유지해야 하는 정보 — 어느 포트로 돌아갈지, 그리고 같은 장치인지 확인할 시리얼
    private string? _reconnectPort;
    private string? _expectedSerial;
    private int _reconnectAttempts;

    public MainViewModel()
    {
        ConnectCommand = new AsyncRelayCommand(_ => ConnectAsync(),
            _ => !IsConnected && !IsReconnecting && SelectedPort is not null, ReportError);

        // 재접속 대기 중에도 눌러서 취소할 수 있어야 한다
        DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => IsConnected || IsReconnecting);
        RefreshPortsCommand = new RelayCommand(_ => RefreshPorts());
        SelectPresetCommand = new AsyncRelayCommand(p => SelectPresetAsync(ToInt(p)), _ => IsConnected && !OutputEnabled, ReportError);
        OutputOnCommand = new AsyncRelayCommand(_ => SetOutputAsync(true), _ => IsConnected && !OutputEnabled, ReportError);
        OutputOffCommand = new AsyncRelayCommand(_ => SetOutputAsync(false), _ => IsConnected && OutputEnabled, ReportError);
        NudgeVoltsCommand = new AsyncRelayCommand(p => ApplySetpointAsync(SetVolts + ToDouble(p), SetAmps), _ => IsConnected, ReportError);
        NudgeAmpsCommand = new AsyncRelayCommand(p => ApplySetpointAsync(SetVolts, SetAmps + ToDouble(p)), _ => IsConnected, ReportError);
        NudgePdCommand = new RelayCommand(p => StepPdVoltage(ToInt(p)), _ => IsConnected && !OutputEnabled);
        SetPdVoltageCommand = new AsyncRelayCommand(_ => SetPdVoltageAsync(), _ => IsConnected && !OutputEnabled, ReportError);
        SaveConfigCommand = new AsyncRelayCommand(_ => SaveConfigAsync(), _ => IsConnected, ReportError);
        ClearHistoryCommand = new RelayCommand(_ => History.Clear());
        ToggleTrendCommand = new RelayCommand(_ => IsTrendVisible = !IsTrendVisible);
        ShowMonitorCommand = new RelayCommand(_ => ActiveView = AppView.Monitor);
        ShowSetupCommand = new RelayCommand(_ => ActiveView = AppView.Setup);
        ShowLogCommand = new RelayCommand(_ => ActiveView = AppView.Log);
        OcpOnCommand = new AsyncRelayCommand(_ => SetOcpAsync(true), _ => IsConnected && !IsOcpEnabled, ReportError);
        OcpOffCommand = new AsyncRelayCommand(_ => SetOcpAsync(false), _ => IsConnected && IsOcpEnabled, ReportError);

        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += async (_, _) => await PollAsync().ConfigureAwait(true);

        _reconnectTimer = new DispatcherTimer { Interval = ReconnectInterval };
        _reconnectTimer.Tick += async (_, _) =>
        {
            try
            {
                await TryReconnectAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        };

        _brightnessDebounce = new DispatcherTimer { Interval = BrightnessWriteDelay };
        _brightnessDebounce.Tick += async (_, _) =>
        {
            _brightnessDebounce.Stop();
            try
            {
                await ApplyBrightnessAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
        };

        RefreshPorts();
    }

    // ── 연결 ─────────────────────────────────────────────────────────────

    public ObservableCollection<string> AvailablePorts { get; } = [];

    private string? _selectedPort;
    public string? SelectedPort
    {
        get => _selectedPort;
        set { if (SetField(ref _selectedPort, value)) RaiseCommandStates(); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetField(ref _isConnected, value)) return;
            OnPropertyChanged(nameof(ConnectionState));
            OnPropertyChanged(nameof(LinkLabel));
            RaiseCommandStates();
        }
    }

    /// <summary>USB가 빠졌거나 응답이 없어 재접속을 기다리는 중.</summary>
    private bool _isReconnecting;
    public bool IsReconnecting
    {
        get => _isReconnecting;
        private set
        {
            if (!SetField(ref _isReconnecting, value)) return;
            OnPropertyChanged(nameof(ConnectionState));
            OnPropertyChanged(nameof(LinkLabel));
            RaiseCommandStates();
        }
    }

    /// <summary>헤더 상태 칩 표시용 — RUN / IDLE / RECONNECT / OFFLINE.</summary>
    public string ConnectionState =>
        IsConnected ? (OutputEnabled ? "RUN" : "IDLE")
        : IsReconnecting ? "RECONNECT"
        : "OFFLINE";

    /// <summary>PORT 카드용 링크 상태. 헤더 칩과 중복되지 않게 출력 상태는 섞지 않는다.</summary>
    public string LinkLabel =>
        IsConnected ? "LINKED"
        : IsReconnecting ? "RECONNECT"
        : "OFFLINE";

    // ── 장치 정보 ────────────────────────────────────────────────────────

    private string _deviceName = "—";
    public string DeviceName { get => _deviceName; private set => SetField(ref _deviceName, value); }

    private string _firmwareVersion = "—";
    public string FirmwareVersion { get => _firmwareVersion; private set => SetField(ref _firmwareVersion, value); }

    private string _serialNumber = "—";
    public string SerialNumber { get => _serialNumber; private set => SetField(ref _serialNumber, value); }

    // ── 실측값 ───────────────────────────────────────────────────────────

    private double _measuredVolts;
    public double MeasuredVolts
    {
        get => _measuredVolts;
        private set { if (SetField(ref _measuredVolts, value)) OnPropertyChanged(nameof(MeasuredWatts)); }
    }

    private double _measuredAmps;
    public double MeasuredAmps
    {
        get => _measuredAmps;
        private set { if (SetField(ref _measuredAmps, value)) OnPropertyChanged(nameof(MeasuredWatts)); }
    }

    public double MeasuredWatts => MeasuredVolts * MeasuredAmps;

    private OutputRegulation _regulation;
    public OutputRegulation Regulation
    {
        get => _regulation;
        private set { if (SetField(ref _regulation, value)) OnPropertyChanged(nameof(RegulationLabel)); }
    }

    /// <summary>디자인의 초소형 배지용 약어 — 열거형 이름을 그대로 쓰면 너무 길다.</summary>
    public string RegulationLabel => Regulation switch
    {
        OutputRegulation.ConstantCurrent => "CC",
        OutputRegulation.OverCurrent => "OC",
        _ => "CV",
    };

    private bool _outputEnabled;
    public bool OutputEnabled
    {
        get => _outputEnabled;
        private set
        {
            if (!SetField(ref _outputEnabled, value)) return;
            OnPropertyChanged(nameof(ConnectionState));
            RaiseCommandStates();
        }
    }

    /// <summary>Trend 차트용 시계열. 오래된 샘플부터 버린다.</summary>
    public ObservableCollection<MeasurementSample> History { get; } = [];

    // ── 설정값 ───────────────────────────────────────────────────────────

    private double _setVolts = 5.0;
    public double SetVolts { get => _setVolts; private set => SetField(ref _setVolts, value); }

    private double _setAmps = 1.0;
    public double SetAmps { get => _setAmps; private set => SetField(ref _setAmps, value); }

    private int _activePresetId;
    public int ActivePresetId { get => _activePresetId; private set => SetField(ref _activePresetId, value); }

    public ObservableCollection<PresetItem> Presets { get; } =
        [.. Enumerable.Range(0, PdPowerDevice.PresetCount).Select(id => new PresetItem(id))];

    // ── 입력 (PD) ────────────────────────────────────────────────────────

    private string _inputState = "—";
    public string InputState { get => _inputState; private set => SetField(ref _inputState, value); }

    private double _inputVolts;
    public double InputVolts { get => _inputVolts; private set => SetField(ref _inputVolts, value); }

    /// <summary>PD INPUT 카드의 요청 전압 단계. 프로토콜 하한이 8 V라 9 V부터 시작한다.</summary>
    public int[] PdVoltageOptions { get; } = [9, 12, 15, 20];

    private int _selectedPdVoltage = 20;
    public int SelectedPdVoltage { get => _selectedPdVoltage; set => SetField(ref _selectedPdVoltage, value); }

    /// <summary>PD 전압 스테퍼 — 임의 값이 아니라 표준 단계 사이를 이동한다.</summary>
    private void StepPdVoltage(int direction)
    {
        int index = Array.IndexOf(PdVoltageOptions, SelectedPdVoltage);
        if (index < 0) index = PdVoltageOptions.Length - 1;
        SelectedPdVoltage = PdVoltageOptions[Math.Clamp(index + direction, 0, PdVoltageOptions.Length - 1)];
    }

    // ── Setup: 과전류 보호 ───────────────────────────────────────────────

    /// <summary>
    /// OCP. 임계값은 별도 설정이 아니라 현재 프리셋의 전류값이고,
    /// 초과 후 약 200 ms 뒤 출력이 차단된다(벤더 README). 휘발성 — 유지하려면 설정 저장 필요.
    /// </summary>
    private bool _isOcpEnabled;
    public bool IsOcpEnabled
    {
        get => _isOcpEnabled;
        private set { if (SetField(ref _isOcpEnabled, value)) RaiseCommandStates(); }
    }

    // ── Setup: LCD 밝기 ──────────────────────────────────────────────────

    public const int MinBrightness = 1;
    public const int MaxBrightness = 100;

    /// <summary>
    /// LCD 밝기(%). 슬라이더를 끌면 값이 연속으로 바뀌므로 <see cref="BrightnessWriteDelay"/>
    /// 만큼 모아서 한 번만 장치에 쓴다.
    /// </summary>
    private int _brightnessPercent = 50;
    public int BrightnessPercent
    {
        get => _brightnessPercent;
        set
        {
            int clamped = Math.Clamp(value, MinBrightness, MaxBrightness);
            if (!SetField(ref _brightnessPercent, clamped)) return;

            // 장치에서 읽어와 채우는 중이면 되쓰지 않는다
            if (_suppressBrightnessWrite || _device is null) return;

            _brightnessDebounce.Stop();
            _brightnessDebounce.Start();
        }
    }

    // ── Setup: 설정 저장 ─────────────────────────────────────────────────

    /// <summary>
    /// 앱이 휘발성 항목을 건드린 뒤 아직 <c>SYSTEM_CONFIG_SAVE</c>(0x44)를 보내지 않은 상태.
    /// 장치 플래시를 읽는 명령이 없으므로 <b>"연결 이후 앱이 만든 변경"</b>만 추적한다 —
    /// 장치 노브로 바꾼 값이나 연결 전 상태는 알 수 없다.
    /// </summary>
    private bool _hasUnsavedChanges;
    public bool HasUnsavedChanges { get => _hasUnsavedChanges; private set => SetField(ref _hasUnsavedChanges, value); }

    // ── 상태 메시지 / 로그 ───────────────────────────────────────────────

    private string _statusMessage = "포트를 선택하고 연결하세요.";
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }

    public ObservableCollection<string> Log { get; } = [];

    // ── 화면 전환 (레일 내비) ────────────────────────────────────────────

    private AppView _activeView = AppView.Monitor;
    public AppView ActiveView
    {
        get => _activeView;
        private set
        {
            if (!SetField(ref _activeView, value)) return;
            OnPropertyChanged(nameof(IsMonitorView));
            OnPropertyChanged(nameof(IsSetupView));
            OnPropertyChanged(nameof(IsLogView));
        }
    }

    public bool IsMonitorView => ActiveView == AppView.Monitor;
    public bool IsSetupView => ActiveView == AppView.Setup;
    public bool IsLogView => ActiveView == AppView.Log;

    /// <summary>Trend 카드 접기 — 디자인의 Hide / Show trend graph 동작.</summary>
    private bool _isTrendVisible = true;
    public bool IsTrendVisible
    {
        get => _isTrendVisible;
        private set { if (SetField(ref _isTrendVisible, value)) OnPropertyChanged(nameof(IsTrendHidden)); }
    }

    public bool IsTrendHidden => !IsTrendVisible;

    /// <summary>
    /// 켜면 모든 원시 프레임을 Log에 남긴다. 250 ms 폴링당 6~8 프레임이 오가므로 기본은 끔.
    /// </summary>
    private bool _isFrameTraceEnabled;
    public bool IsFrameTraceEnabled { get => _isFrameTraceEnabled; set => SetField(ref _isFrameTraceEnabled, value); }

    // ── 커맨드 ───────────────────────────────────────────────────────────

    public AsyncRelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshPortsCommand { get; }
    public AsyncRelayCommand SelectPresetCommand { get; }
    public AsyncRelayCommand OutputOnCommand { get; }
    public AsyncRelayCommand OutputOffCommand { get; }
    public AsyncRelayCommand NudgeVoltsCommand { get; }
    public AsyncRelayCommand NudgeAmpsCommand { get; }
    public RelayCommand NudgePdCommand { get; }
    public AsyncRelayCommand SetPdVoltageCommand { get; }
    public AsyncRelayCommand SaveConfigCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ToggleTrendCommand { get; }
    public RelayCommand ShowMonitorCommand { get; }
    public RelayCommand ShowSetupCommand { get; }
    public RelayCommand ShowLogCommand { get; }
    public AsyncRelayCommand OcpOnCommand { get; }
    public AsyncRelayCommand OcpOffCommand { get; }

    // ── 동작 ─────────────────────────────────────────────────────────────

    public void RefreshPorts()
    {
        var names = PdPowerDevice.GetPortNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        AvailablePorts.Clear();
        foreach (var name in names) AvailablePorts.Add(name);
        SelectedPort = names.Contains(SelectedPort) ? SelectedPort : names.FirstOrDefault();
    }

    private async Task ConnectAsync()
    {
        if (SelectedPort is null) return;

        StopReconnect();
        var device = OpenDevice(SelectedPort);

        try
        {
            var info = await device.ReadDeviceInfoAsync().ConfigureAwait(true);
            await AdoptDeviceAsync(device, info).ConfigureAwait(true);
            StatusMessage = $"{SelectedPort} 연결됨 — {info.Name}";
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>사용자가 명시적으로 끊는 경우 — 재접속 대기까지 취소한다.</summary>
    public void Disconnect()
    {
        bool wasWaiting = IsReconnecting;
        StopReconnect();
        TeardownLink(transient: false);
        StatusMessage = wasWaiting ? "재접속 대기를 취소했습니다." : "연결이 해제되었습니다.";
    }

    private PdPowerDevice OpenDevice(string portName)
    {
        var device = new PdPowerDevice(portName);
        try
        {
            device.FrameExchanged += OnFrameExchanged;
            device.Open();
            return device;
        }
        catch
        {
            // Open 이 실패하면 호출자에게 인스턴스가 전달되지 않으므로 여기서 정리해야 한다
            device.FrameExchanged -= OnFrameExchanged;
            device.Dispose();
            throw;
        }
    }

    private void OnFrameExchanged(object? sender, FrameTrace trace)
    {
        if (IsFrameTraceEnabled) AppendLog(trace.ToString());
    }

    /// <summary>열려 있는 장치를 이 ViewModel 의 것으로 받아들이고 폴링을 시작한다.</summary>
    private async Task AdoptDeviceAsync(PdPowerDevice device, DeviceInfo info)
    {
        DeviceName = info.Name;
        FirmwareVersion = info.FirmwareVersion;
        SerialNumber = info.SerialNumber;

        _device = device;
        IsConnected = true;
        await RefreshSettingsAsync().ConfigureAwait(true);
        _pollTimer.Start();
    }

    /// <summary>
    /// 포트를 닫고 실시간 상태를 비운다.
    /// <paramref name="transient"/> 면 장치 식별 정보와 Trend 히스토리를 남긴다 —
    /// USB가 잠깐 빠진 것뿐이라면 돌아왔을 때 그래프가 이어지는 편이 낫다.
    /// </summary>
    private void TeardownLink(bool transient)
    {
        _pollTimer.Stop();
        _brightnessDebounce.Stop();

        if (_device is not null)
        {
            _device.FrameExchanged -= OnFrameExchanged;
            _device.Dispose();
            _device = null;
        }

        IsConnected = false;
        OutputEnabled = false;
        IsOcpEnabled = false;
        HasUnsavedChanges = false;
        MeasuredVolts = MeasuredAmps = InputVolts = 0;
        Regulation = OutputRegulation.ConstantVoltage;
        InputState = "—";

        if (!transient)
        {
            DeviceName = FirmwareVersion = SerialNumber = "—";
            History.Clear();
        }
    }

    // ── 재접속 대기 ──────────────────────────────────────────────────────

    /// <summary>통신이 끊어졌을 때 같은 포트로 돌아오기를 기다린다.</summary>
    private void BeginReconnect(string reason)
    {
        // 어느 포트로 돌아갈지, 그리고 같은 장치인지 판별할 시리얼을 먼저 붙잡아 둔다
        _reconnectPort = SelectedPort;
        _expectedSerial = SerialNumber is "—" or "" ? null : SerialNumber;

        TeardownLink(transient: true);

        if (_reconnectPort is null)
        {
            StatusMessage = $"연결이 끊어졌습니다: {reason}";
            return;
        }

        _reconnectAttempts = 0;
        IsReconnecting = true;
        _reconnectTimer.Start();
        AppendLog($"LINK LOST {reason} — {_reconnectPort} 재접속 대기");
        StatusMessage = $"연결이 끊어졌습니다 — {_reconnectPort} 재접속 대기 중… ({reason})";
    }

    private void StopReconnect()
    {
        _reconnectTimer.Stop();
        IsReconnecting = false;
        _reconnectPort = null;
        _expectedSerial = null;
        _reconnectAttempts = 0;
    }

    private async Task TryReconnectAsync()
    {
        if (_reconnectPort is null) { StopReconnect(); return; }

        _reconnectAttempts++;
        if (_reconnectAttempts > MaxReconnectAttempts)
        {
            string port = _reconnectPort;
            StopReconnect();
            TeardownLink(transient: false);
            StatusMessage = $"{port} 재접속에 실패했습니다 " +
                            $"({MaxReconnectAttempts * ReconnectInterval.TotalSeconds:F0}초 초과). 수동으로 연결하세요.";
            return;
        }

        // 포트가 아직 열거로 돌아오지 않았으면 열어볼 필요도 없다
        if (!PdPowerDevice.GetPortNames().Contains(_reconnectPort, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = $"{_reconnectPort} 대기 중… ({_reconnectAttempts}/{MaxReconnectAttempts})";
            return;
        }

        PdPowerDevice? device = null;
        try
        {
            device = OpenDevice(_reconnectPort);
            var info = await device.ReadDeviceInfoAsync().ConfigureAwait(true);

            // 같은 포트 이름에 다른 장치가 꽂힐 수 있다. 시리얼이 다르면 붙지 않고 멈춘다 —
            // 엉뚱한 전원 장치에 프리셋을 쓰는 것보다 사용자가 직접 고르는 게 안전하다.
            if (_expectedSerial is not null && !string.Equals(info.SerialNumber, _expectedSerial, StringComparison.OrdinalIgnoreCase))
            {
                device.FrameExchanged -= OnFrameExchanged;
                device.Dispose();
                string port = _reconnectPort;
                StopReconnect();
                TeardownLink(transient: false);
                StatusMessage = $"{port} 에 다른 장치가 있습니다 (SN {info.SerialNumber}) — 자동 재접속을 중단했습니다.";
                return;
            }

            await AdoptDeviceAsync(device, info).ConfigureAwait(true);
            int attempts = _reconnectAttempts;
            StopReconnect();
            AppendLog($"LINK RESTORED {info.Name}");
            StatusMessage = $"재접속했습니다 — {info.Name} ({attempts}회 시도)";
        }
        catch (PdPowerException)
        {
            // 포트는 보이지만 아직 응답할 준비가 안 된 상태 — 다음 주기에 다시 시도한다.
            // AdoptDeviceAsync 도중에 터졌다면 이미 _device 로 채택된 상태이므로 링크째로 정리한다.
            if (ReferenceEquals(_device, device))
            {
                TeardownLink(transient: true);
            }
            else if (device is not null)
            {
                device.FrameExchanged -= OnFrameExchanged;
                device.Dispose();
            }

            StatusMessage = $"{_reconnectPort} 응답 없음 — 재시도 중… ({_reconnectAttempts}/{MaxReconnectAttempts})";
        }
    }

    /// <summary>프리셋 전체와 현재 설정을 장치에서 다시 읽어온다.</summary>
    private async Task RefreshSettingsAsync()
    {
        if (_device is null) return;

        ActivePresetId = await _device.ReadActivePresetIdAsync().ConfigureAwait(true);
        foreach (var item in Presets)
        {
            var preset = await _device.ReadPresetAsync(item.Id).ConfigureAwait(true);
            item.Update(preset.Volts, preset.Amps, preset.PresetId == ActivePresetId);
        }

        var active = Presets[ActivePresetId];
        SetVolts = active.Volts;
        SetAmps = active.Amps;

        IsOcpEnabled = await _device.ReadOcpEnabledAsync().ConfigureAwait(true);
        await LoadBrightnessAsync().ConfigureAwait(true);

        // 방금 읽어온 값이 곧 장치의 현재 상태 — 앱이 바꾼 건 아직 없다
        HasUnsavedChanges = false;
    }

    private async Task PollAsync()
    {
        if (_device is null || _polling) return;
        _polling = true;
        try
        {
            var measurement = await _device.ReadMeasurementAsync().ConfigureAwait(true);
            var status = await _device.ReadOutputStatusAsync().ConfigureAwait(true);

            MeasuredVolts = measurement.Volts;
            MeasuredAmps = measurement.Amps;
            OutputEnabled = status.Enabled;
            Regulation = status.Regulation;

            History.Add(new MeasurementSample(DateTime.Now, measurement.Volts, measurement.Amps));
            while (History.Count > HistoryCapacity) History.RemoveAt(0);

            try
            {
                var input = await _device.ReadInputStatusAsync().ConfigureAwait(true);
                InputState = input.State.ToString().ToUpperInvariant();
                InputVolts = input.Volts;
            }
            catch (PdPowerException)
            {
                // INPUT_STATE는 펌웨어 v1.0.2.0 이상에서만 지원 — 없으면 조용히 건너뛴다.
                InputState = "N/A";
            }
        }
        catch (PdPowerException ex)
        {
            // USB가 빠졌거나 장치가 재부팅된 경우 — 끊고 끝내지 않고 돌아오기를 기다린다
            AppendLog($"ERROR {ex.Message}");
            BeginReconnect(ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task SelectPresetAsync(int presetId)
    {
        if (_device is null) return;

        await _device.SetActivePresetIdAsync(presetId).ConfigureAwait(true);
        ActivePresetId = presetId;
        foreach (var item in Presets) item.IsActive = item.Id == presetId;

        SetVolts = Presets[presetId].Volts;
        SetAmps = Presets[presetId].Amps;
        HasUnsavedChanges = true;
        StatusMessage = $"M{presetId} 적용 — {SetVolts:F3} V / {SetAmps:F3} A";
    }

    private async Task ApplySetpointAsync(double volts, double amps)
    {
        if (_device is null) return;

        volts = Math.Clamp(Math.Round(volts, 3), PdPowerDevice.MinVolts, PdPowerDevice.MaxVolts);
        amps = Math.Clamp(Math.Round(amps, 3), PdPowerDevice.MinAmps, PdPowerDevice.MaxAmps);

        await _device.WritePresetAsync(ActivePresetId, volts, amps).ConfigureAwait(true);
        SetVolts = volts;
        SetAmps = amps;
        Presets[ActivePresetId].Update(volts, amps, isActive: true);
        HasUnsavedChanges = true;
        StatusMessage = $"M{ActivePresetId} → {volts:F3} V / {amps:F3} A (저장하지 않으면 휘발)";
    }

    /// <summary>디바운스가 끝난 뒤 실제 밝기 쓰기. 되읽은 값으로 슬라이더를 맞춘다.</summary>
    private async Task ApplyBrightnessAsync()
    {
        if (_device is null) return;

        await _device.SetBrightnessAsync(BrightnessPercent).ConfigureAwait(true);
        await LoadBrightnessAsync().ConfigureAwait(true);
        HasUnsavedChanges = true;
        StatusMessage = $"LCD 밝기 {BrightnessPercent}%";
    }

    /// <summary>장치 값으로 슬라이더를 채운다. 되쓰기 루프가 생기지 않게 억제 플래그를 세운다.</summary>
    private async Task LoadBrightnessAsync()
    {
        if (_device is null) return;

        int percent = await _device.ReadBrightnessAsync().ConfigureAwait(true);
        _suppressBrightnessWrite = true;
        try
        {
            BrightnessPercent = percent;
        }
        finally
        {
            _suppressBrightnessWrite = false;
        }
    }

    /// <summary>OCP를 바꾸고 장치에서 되읽어 확인한다 — 쓰기만 하면 실제 반영 여부를 알 수 없다.</summary>
    private async Task SetOcpAsync(bool enabled)
    {
        if (_device is null) return;

        await _device.SetOcpEnabledAsync(enabled).ConfigureAwait(true);
        IsOcpEnabled = await _device.ReadOcpEnabledAsync().ConfigureAwait(true);
        HasUnsavedChanges = true;
        StatusMessage = IsOcpEnabled
            ? $"OCP 켜짐 — {SetAmps:F3} A 초과 시 약 200 ms 후 출력 차단"
            : "OCP 꺼짐 — 전류 제한에 걸리면 CC 동작(출력 유지)";
    }

    private async Task SetOutputAsync(bool enabled)
    {
        if (_device is null) return;

        await _device.SetOutputEnabledAsync(enabled).ConfigureAwait(true);
        StatusMessage = enabled ? "출력 ON" : "출력 OFF";
    }

    private async Task SetPdVoltageAsync()
    {
        if (_device is null) return;

        await _device.SetPdRequestVoltageAsync(SelectedPdVoltage).ConfigureAwait(true);
        HasUnsavedChanges = true;
        StatusMessage = $"PD 입력 {SelectedPdVoltage} V 요청 — 출력 OFF & 출력전압 5 V 미만일 때만 반영됩니다.";
    }

    private async Task SaveConfigAsync()
    {
        if (_device is null) return;

        await _device.SaveConfigAsync().ConfigureAwait(true);
        HasUnsavedChanges = false;
        StatusMessage = "현재 설정을 장치 플래시에 저장했습니다.";
    }

    /// <summary>
    /// PdPowerDevice.FrameExchanged 는 스레드 풀에서 올라오므로 UI 스레드로 넘겨야 한다
    /// (ObservableCollection 을 다른 스레드에서 고치면 NotSupportedException).
    /// </summary>
    private void AppendLog(string line)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AppendLog(line));
            return;
        }

        Log.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
        while (Log.Count > LogCapacity) Log.RemoveAt(0);
    }

    private void ReportError(Exception ex)
    {
        StatusMessage = ex is PdPowerException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
        AppendLog($"ERROR {StatusMessage}");
    }

    private void RaiseCommandStates()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        SelectPresetCommand.RaiseCanExecuteChanged();
        OutputOnCommand.RaiseCanExecuteChanged();
        OutputOffCommand.RaiseCanExecuteChanged();
        OcpOnCommand.RaiseCanExecuteChanged();
        OcpOffCommand.RaiseCanExecuteChanged();
        NudgeVoltsCommand.RaiseCanExecuteChanged();
        NudgeAmpsCommand.RaiseCanExecuteChanged();
        NudgePdCommand.RaiseCanExecuteChanged();
        SetPdVoltageCommand.RaiseCanExecuteChanged();
        SaveConfigCommand.RaiseCanExecuteChanged();
    }

    private static int ToInt(object? parameter)
        => parameter is null ? 0 : Convert.ToInt32(parameter, CultureInfo.InvariantCulture);

    private static double ToDouble(object? parameter)
        => parameter is null ? 0 : Convert.ToDouble(parameter, CultureInfo.InvariantCulture);

    public void Dispose() => Disconnect();
}

/// <summary>레일 내비가 전환하는 화면. 목업의 접힌 레일 코드 MO / ST / LG 에 대응한다.</summary>
public enum AppView { Monitor, Setup, Log }

/// <summary>좌측 레일의 프리셋 한 줄.</summary>
public sealed class PresetItem(int id) : ObservableObject
{
    public int Id { get; } = id;

    public string Label => $"M{Id}";

    private double _volts;
    public double Volts
    {
        get => _volts;
        private set { if (SetField(ref _volts, value)) OnPropertyChanged(nameof(Summary)); }
    }

    private double _amps;
    public double Amps
    {
        get => _amps;
        private set { if (SetField(ref _amps, value)) OnPropertyChanged(nameof(Summary)); }
    }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => SetField(ref _isActive, value); }

    /// <summary>
    /// 레일 폭(196 px)에 스크롤바가 생겨도 잘리지 않게 최대한 짧게 — "3.3V · 0.5A".
    /// </summary>
    public string Summary => $"{Volts:0.##}V · {Amps:0.##}A";

    public void Update(double volts, double amps, bool isActive)
    {
        Volts = volts;
        Amps = amps;
        IsActive = isActive;
    }
}

/// <summary>Trend 차트 한 점.</summary>
public sealed record MeasurementSample(DateTime Timestamp, double Volts, double Amps);
