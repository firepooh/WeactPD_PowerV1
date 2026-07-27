namespace PdPower.Core.Models;

/// <summary>WHO_AM_I / SYSTEM_VERSION / SYSTEM_SERIAL_NUM 조회 결과.</summary>
public sealed record DeviceInfo(string Name, string FirmwareVersion, string SerialNumber);

/// <summary>READ_OUTPUT_STATE(0x82) 해석 결과.</summary>
public sealed record OutputStatus(bool Enabled, OutputRegulation Regulation)
{
    /// <summary>원본 상태 바이트 — 로그·디버깅용.</summary>
    public byte Raw { get; init; }
}

/// <summary>READ_OUTPUT_DISPLAY(0x85) 실측값.</summary>
public sealed record OutputMeasurement(double Volts, double Amps)
{
    public double Watts => Volts * Amps;
}

/// <summary>OUTPUT_DATA(0x04/0x84) 프리셋 설정값.</summary>
public sealed record OutputSetpoint(int PresetId, double Volts, double Amps);

/// <summary>READ_INPUT_STATE(0x8A) 입력 상태.</summary>
/// <param name="State">입력 협상 상태.</param>
/// <param name="Volts">실제 입력 전압.</param>
/// <param name="RequestedPdVolts">장치가 요청 중인 PD 전압.</param>
public sealed record InputStatus(InputState State, double Volts, double RequestedPdVolts);
