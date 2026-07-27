using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using PdPower.Core.Models;
using PdPower.Core.Protocol;

namespace PdPower.Core;

/// <summary>
/// WeAct PD Power Mini V1 (Buck) 장치 제어.
/// </summary>
/// <remarks>
/// 장치는 요청 없이 데이터를 보내지 않으므로 단순 요청/응답 모델로 다룬다.
/// 모든 공개 메서드는 세마포어로 직렬화되므로 여러 스레드에서 호출해도 안전하다.
/// </remarks>
public sealed class PdPowerDevice : IDisposable
{
    public const double MinVolts = 1.0;
    public const double MaxVolts = 20.0;
    public const double MinAmps = 0.0;
    public const double MaxAmps = 3.0;

    /// <summary>프리셋 그룹 M0–M4.</summary>
    public const int PresetCount = 5;

    /// <summary>PD 입력 전압 요청 하한 — 이보다 낮은 값은 장치가 무시한다.</summary>
    public const double MinPdRequestVolts = 8.0;

    private readonly SerialPort _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public PdPowerDevice(string portName, ProtocolMode mode = ProtocolMode.UsbCdc, int baudRate = 115200)
    {
        PortName = portName;
        Mode = mode;
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
        };
    }

    public string PortName { get; }

    public ProtocolMode Mode { get; }

    public bool IsOpen => !_disposed && _port.IsOpen;

    /// <summary>응답 대기 제한(ms).</summary>
    public int TimeoutMs
    {
        get => _port.ReadTimeout;
        set => _port.ReadTimeout = _port.WriteTimeout = value;
    }

    /// <summary>
    /// OUTPUT_EN(0x02) 페이로드에서 "출력 켜기"를 뜻하는 값.
    /// 제조사 xlsx는 <c>0=enable</c>, 제조사 Python 예제는 <c>1=enable</c>로 서로 상충한다.
    /// 기본값은 실행 가능한 예제 코드 기준(1). 실장비 확인 후 다르면 이 값을 바꾼다.
    /// </summary>
    public byte OutputEnableOnValue { get; set; } = 0x01;

    /// <summary>
    /// 주고받은 원시 프레임을 흘려보낸다 — Log 화면·디버깅용.
    /// <b>스레드 풀 스레드에서 발생하므로</b> UI 컬렉션을 직접 건드리면 안 된다.
    /// 구독자가 디스패처로 마샬링할 책임을 진다.
    /// </summary>
    public event EventHandler<FrameTrace>? FrameExchanged;

    public static string[] GetPortNames() => SerialPort.GetPortNames();

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_port.IsOpen) return;

        try
        {
            _port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
        {
            throw new PdPowerException($"{PortName} 포트를 열 수 없습니다: {ex.Message}", ex);
        }

        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
    }

    public void Close()
    {
        if (_port.IsOpen) _port.Close();
    }

    // ── 장치 정보 ────────────────────────────────────────────────────────

    public Task<string> ReadNameAsync(CancellationToken ct = default)
        => TransactAsync(() => ReadAsciiResponse(PdCommand.WhoAmI), ct);

    public Task<string> ReadFirmwareVersionAsync(CancellationToken ct = default)
        => TransactAsync(() => ReadAsciiResponse(PdCommand.SystemVersion), ct);

    public Task<string> ReadSerialNumberAsync(CancellationToken ct = default)
        => TransactAsync(() => ReadAsciiResponse(PdCommand.SystemSerialNumber), ct);

    public async Task<DeviceInfo> ReadDeviceInfoAsync(CancellationToken ct = default)
        => new(await ReadNameAsync(ct).ConfigureAwait(false),
               await ReadFirmwareVersionAsync(ct).ConfigureAwait(false),
               await ReadSerialNumberAsync(ct).ConfigureAwait(false));

    // ── 출력 상태 ────────────────────────────────────────────────────────

    /// <summary>READ_OUTPUT_STATE(0x82) — 출력 on/off와 CV/CC/OC 판정.</summary>
    public Task<OutputStatus> ReadOutputStatusAsync(CancellationToken ct = default)
        => TransactAsync(() =>
        {
            byte raw = ReadFixedResponse(PdCommand.OutputEnable)[0];
            var regulation = (OutputRegulation)((raw >> 1) & 0b11);
            return new OutputStatus((raw & 0b1) != 0, regulation) { Raw = raw };
        }, ct);

    public Task SetOutputEnabledAsync(bool enabled, CancellationToken ct = default)
        => WriteAsync(PdCommand.OutputEnable,
                      [enabled ? OutputEnableOnValue : (byte)(OutputEnableOnValue ^ 0x01)], ct);

    /// <summary>READ_OUTPUT_DISPLAY(0x85) — 실측 전압/전류. 모니터링 폴링에 쓴다.</summary>
    public Task<OutputMeasurement> ReadMeasurementAsync(CancellationToken ct = default)
        => TransactAsync(() =>
        {
            var payload = ReadFixedResponse(PdCommand.OutputDisplay);
            return new OutputMeasurement(
                Frame.ReadUInt16(payload, 0) / 1000.0,
                Frame.ReadUInt16(payload, 2) / 1000.0);
        }, ct);

    // ── 프리셋 (M0–M4) ───────────────────────────────────────────────────

    public Task<int> ReadActivePresetIdAsync(CancellationToken ct = default)
        => TransactAsync(() => (int)ReadFixedResponse(PdCommand.OutputId)[0], ct);

    public Task SetActivePresetIdAsync(int presetId, CancellationToken ct = default)
    {
        ValidatePresetId(presetId);
        return WriteAsync(PdCommand.OutputId, [(byte)presetId], ct);
    }

    public Task<OutputSetpoint> ReadPresetAsync(int presetId, CancellationToken ct = default)
    {
        ValidatePresetId(presetId);
        return TransactAsync(() =>
        {
            var payload = ReadFixedResponse(PdCommand.OutputData, (byte)presetId);
            return new OutputSetpoint(
                payload[0],
                Frame.ReadUInt16(payload, 1) / 1000.0,
                Frame.ReadUInt16(payload, 3) / 1000.0);
        }, ct);
    }

    public Task WritePresetAsync(int presetId, double volts, double amps, CancellationToken ct = default)
    {
        ValidatePresetId(presetId);
        if (volts is < MinVolts or > MaxVolts)
            throw new ArgumentOutOfRangeException(nameof(volts), volts, $"{MinVolts}–{MaxVolts} V 범위를 벗어났습니다.");
        if (amps is < MinAmps or > MaxAmps)
            throw new ArgumentOutOfRangeException(nameof(amps), amps, $"{MinAmps}–{MaxAmps} A 범위를 벗어났습니다.");

        int mv = (int)Math.Round(volts * 1000);
        int ma = (int)Math.Round(amps * 1000);
        return WriteAsync(PdCommand.OutputData,
                          [(byte)presetId, .. Frame.WriteUInt16(mv), .. Frame.WriteUInt16(ma)], ct);
    }

    // ── 입력 (PD/QC/DC) ──────────────────────────────────────────────────

    public Task<InputStatus> ReadInputStatusAsync(CancellationToken ct = default)
        => TransactAsync(() =>
        {
            var payload = ReadFixedResponse(PdCommand.InputState);
            return new InputStatus(
                (InputState)payload[0],
                Frame.ReadUInt16(payload, 1) / 1000.0,
                Frame.ReadUInt16(payload, 3) / 10.0);
        }, ct);

    /// <summary>
    /// PD 소스에 요청할 입력 전압을 바꾼다.
    /// 장치는 <b>출력이 OFF이고 출력 전압이 5 V 미만</b>일 때만 이 값을 반영한다.
    /// </summary>
    public Task SetPdRequestVoltageAsync(double volts, CancellationToken ct = default)
    {
        if (volts < MinPdRequestVolts)
            throw new ArgumentOutOfRangeException(nameof(volts), volts, $"{MinPdRequestVolts} V 이상이어야 합니다.");

        return WriteAsync(PdCommand.InputState, Frame.WriteUInt16((int)Math.Round(volts * 10)), ct);
    }

    // ── 보호·표시 설정 ───────────────────────────────────────────────────

    /// <summary>과전류 보호(OCP).</summary>
    public Task<bool> ReadOcpEnabledAsync(CancellationToken ct = default)
        => ReadFlagAsync(PdCommand.OutputOcpEnable, ct);

    public Task SetOcpEnabledAsync(bool enabled, CancellationToken ct = default)
        => WriteFlagAsync(PdCommand.OutputOcpEnable, enabled, ct);

    /// <summary>출력 오프셋 보정.</summary>
    public Task<bool> ReadOffsetEnabledAsync(CancellationToken ct = default)
        => ReadFlagAsync(PdCommand.OutputOffsetEnable, ct);

    public Task SetOffsetEnabledAsync(bool enabled, CancellationToken ct = default)
        => WriteFlagAsync(PdCommand.OutputOffsetEnable, enabled, ct);

    /// <summary>출력 OFF 시 잔류 전압 방전.</summary>
    public Task SetDischargeEnabledAsync(bool enabled, CancellationToken ct = default)
        => WriteFlagAsync(PdCommand.OutputDischargeEnable, enabled, ct);

    /// <summary>LCD 밝기 1–100.</summary>
    public Task<int> ReadBrightnessAsync(CancellationToken ct = default)
        => TransactAsync(() => (int)ReadFixedResponse(PdCommand.Brightness)[0], ct);

    public Task SetBrightnessAsync(int percent, CancellationToken ct = default)
    {
        if (percent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "1–100 범위여야 합니다.");
        return WriteAsync(PdCommand.Brightness, [(byte)percent], ct);
    }

    // ── 시스템 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 휘발성 설정(프리셋 ID/값, OCP, 오프셋, 밝기, PD 전압)을 장치 플래시에 저장한다.
    /// 저장하지 않으면 전원 재인가 시 이전 값으로 돌아간다.
    /// </summary>
    public Task SaveConfigAsync(CancellationToken ct = default)
        => WriteAsync(PdCommand.SystemConfigSave, [], ct);

    public Task ResetAsync(CancellationToken ct = default)
        => WriteAsync(PdCommand.SystemReset, [], ct);

    public Task FactoryResetAsync(CancellationToken ct = default)
        => WriteAsync(PdCommand.SystemFactoryReset, [], ct);

    // ── 내부: 트랜잭션 ───────────────────────────────────────────────────

    private Task<bool> ReadFlagAsync(PdCommand command, CancellationToken ct)
        => TransactAsync(() => ReadFixedResponse(command)[0] != 0, ct);

    private Task WriteFlagAsync(PdCommand command, bool enabled, CancellationToken ct)
        => WriteAsync(command, [enabled ? (byte)0x01 : (byte)0x00], ct);

    private Task WriteAsync(PdCommand command, byte[] payload, CancellationToken ct)
        => TransactAsync<object?>(() =>
        {
            var frame = Frame.BuildWrite(Mode, command, payload);
            _port.Write(frame, 0, frame.Length);
            FrameExchanged?.Invoke(this, new FrameTrace(FrameDirection.Sent, command, frame));
            return null;
        }, ct);

    /// <summary>포트 접근을 직렬화하고 예외를 도메인 예외로 변환한다.</summary>
    private async Task<T> TransactAsync<T>(Func<T> action, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_port.IsOpen) throw new PdPowerException($"{PortName} 포트가 열려 있지 않습니다.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    return action();
                }
                catch (TimeoutException)
                {
                    throw new PdPowerTimeoutException($"{TimeoutMs} ms 안에 응답이 없습니다.");
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    throw new PdPowerException($"{PortName} 통신 오류: {ex.Message}", ex);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 내부: 프레임 송수신 (세마포어 안에서만 호출) ──────────────────────

    /// <summary>고정 길이 응답 읽기. 요청 전 수신 버퍼를 비워 프레임 동기를 잡는다.</summary>
    private byte[] ReadFixedResponse(PdCommand command, params byte[] requestPayload)
    {
        int length = Frame.ResponseLength(command)
            ?? throw new InvalidOperationException($"{command}는 가변 길이 응답입니다.");

        SendRequest(command, requestPayload);
        var frame = ReadExact(length);
        FrameExchanged?.Invoke(this, new FrameTrace(FrameDirection.Received, command, frame));
        return Frame.ExtractPayload(Mode, command, frame).ToArray();
    }

    /// <summary>
    /// 가변 길이 ASCII 응답 읽기.
    /// USB CDC는 <c>[cmd][ascii…][0x0A]</c>, UART는 <c>[cmd][len][ascii…][crc8]</c> 구조다.
    /// </summary>
    private string ReadAsciiResponse(PdCommand command)
    {
        SendRequest(command);
        byte expectedHead = (byte)((byte)command | Frame.ReadMask);

        if (Mode == ProtocolMode.Uart)
        {
            var header = ReadExact(2);
            if (header[0] != expectedHead)
                throw new PdPowerProtocolException(
                    $"응답 헤더 불일치: 0x{expectedHead:X2} 기대, 0x{header[0]:X2} 수신");

            var body = ReadExact(header[1] + 1); // ASCII + CRC8
            byte[] frame = [.. header, .. body];
            FrameExchanged?.Invoke(this, new FrameTrace(FrameDirection.Received, command, frame));

            byte expectedCrc = Crc8.Compute(frame.AsSpan(0, frame.Length - 1));
            if (frame[^1] != expectedCrc)
                throw new PdPowerProtocolException(
                    $"CRC8 불일치: 0x{expectedCrc:X2} 기대, 0x{frame[^1]:X2} 수신");

            return Encoding.ASCII.GetString(frame, 2, header[1]);
        }

        // USB CDC: 0x0A까지 읽는다. ASCII 페이로드라 0x0A와 충돌하지 않는다.
        var buffer = new List<byte>(64);
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (deadline.ElapsedMilliseconds > TimeoutMs)
                throw new PdPowerTimeoutException($"{TimeoutMs} ms 안에 프레임 종단을 받지 못했습니다.");

            int b = _port.ReadByte();
            if (b == Frame.EndByte) break;
            buffer.Add((byte)b);

            if (buffer.Count > 256)
                throw new PdPowerProtocolException("ASCII 응답이 비정상적으로 깁니다 — 프레임 동기 실패.");
        }

        byte[] received = [.. buffer, Frame.EndByte];
        FrameExchanged?.Invoke(this, new FrameTrace(FrameDirection.Received, command, received));

        if (buffer.Count == 0 || buffer[0] != expectedHead)
            throw new PdPowerProtocolException(
                $"응답 헤더 불일치: 0x{expectedHead:X2} 기대, 0x{(buffer.Count > 0 ? buffer[0] : 0):X2} 수신");

        return Encoding.ASCII.GetString(buffer.ToArray(), 1, buffer.Count - 1);
    }

    private void SendRequest(PdCommand command, params byte[] payload)
    {
        _port.DiscardInBuffer();
        var frame = Frame.BuildRead(Mode, command, payload);
        _port.Write(frame, 0, frame.Length);
        FrameExchanged?.Invoke(this, new FrameTrace(FrameDirection.Sent, command, frame));
    }

    /// <summary>정확히 <paramref name="count"/> 바이트를 읽는다. SerialPort.Read는 부분 읽기가 가능하므로 반복한다.</summary>
    private byte[] ReadExact(int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _port.Read(buffer, read, count - read);
            if (n == 0) throw new PdPowerException("포트가 닫혔습니다.");
            read += n;
        }
        return buffer;
    }

    private static void ValidatePresetId(int presetId)
    {
        if (presetId is < 0 or >= PresetCount)
            throw new ArgumentOutOfRangeException(nameof(presetId), presetId, $"0–{PresetCount - 1} 범위여야 합니다.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Close(); } catch { /* 정리 중 오류는 무시 */ }
        _port.Dispose();
        _gate.Dispose();
    }
}

public enum FrameDirection { Sent, Received }

/// <summary>로그·디버깅용 원시 프레임 기록.</summary>
public sealed record FrameTrace(FrameDirection Direction, PdCommand Command, byte[] Bytes)
{
    public override string ToString()
        => $"{(Direction == FrameDirection.Sent ? "TX" : "RX")} {Command,-22} {Convert.ToHexString(Bytes)}";
}
