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

    private readonly DispatcherTimer _pollTimer;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private PdPowerDevice? _device;
    private bool _polling;

    public MainViewModel()
    {
        ConnectCommand = new AsyncRelayCommand(_ => ConnectAsync(), _ => !IsConnected && SelectedPort is not null, ReportError);
        DisconnectCommand = new RelayCommand(_ => Disconnect(), _ => IsConnected);
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

    /// <summary>헤더 상태 칩 표시용 — RUN / IDLE / OFFLINE.</summary>
    public string ConnectionState => !IsConnected ? "OFFLINE" : OutputEnabled ? "RUN" : "IDLE";

    /// <summary>PORT 카드용 링크 상태. 헤더 칩과 중복되지 않게 출력 상태는 섞지 않는다.</summary>
    public string LinkLabel => IsConnected ? "LINKED" : "OFFLINE";

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

        var device = new PdPowerDevice(SelectedPort);
        device.FrameExchanged += (_, trace) =>
        {
            if (IsFrameTraceEnabled) AppendLog(trace.ToString());
        };

        try
        {
            device.Open();
            var info = await device.ReadDeviceInfoAsync().ConfigureAwait(true);
            DeviceName = info.Name;
            FirmwareVersion = info.FirmwareVersion;
            SerialNumber = info.SerialNumber;

            _device = device;
            IsConnected = true;
            await RefreshSettingsAsync().ConfigureAwait(true);
            _pollTimer.Start();
            StatusMessage = $"{SelectedPort} 연결됨 — {info.Name}";
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public void Disconnect()
    {
        _pollTimer.Stop();
        _device?.Dispose();
        _device = null;

        IsConnected = false;
        OutputEnabled = false;
        IsOcpEnabled = false;
        MeasuredVolts = MeasuredAmps = InputVolts = 0;
        Regulation = OutputRegulation.ConstantVoltage;
        DeviceName = FirmwareVersion = SerialNumber = "—";
        InputState = "—";
        History.Clear();
        StatusMessage = "연결이 해제되었습니다.";
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
            StatusMessage = $"통신 오류로 연결을 해제했습니다: {ex.Message}";
            AppendLog($"ERROR {ex.Message}");
            Disconnect();
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
        StatusMessage = $"M{ActivePresetId} → {volts:F3} V / {amps:F3} A (저장하지 않으면 휘발)";
    }

    /// <summary>OCP를 바꾸고 장치에서 되읽어 확인한다 — 쓰기만 하면 실제 반영 여부를 알 수 없다.</summary>
    private async Task SetOcpAsync(bool enabled)
    {
        if (_device is null) return;

        await _device.SetOcpEnabledAsync(enabled).ConfigureAwait(true);
        IsOcpEnabled = await _device.ReadOcpEnabledAsync().ConfigureAwait(true);
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
        StatusMessage = $"PD 입력 {SelectedPdVoltage} V 요청 — 출력 OFF & 출력전압 5 V 미만일 때만 반영됩니다.";
    }

    private async Task SaveConfigAsync()
    {
        if (_device is null) return;

        await _device.SaveConfigAsync().ConfigureAwait(true);
        StatusMessage = "현재 설정을 장치에 저장했습니다.";
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
