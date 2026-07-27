namespace PdPower.Core.Protocol;

/// <summary>
/// 장치 명령 코드. 읽기 요청은 이 값에 <see cref="Frame.ReadMask"/>(0x80)를 OR 한다.
/// (예: OUTPUT_DATA 쓰기 0x04, 읽기 0x84 / SYSTEM_VERSION 읽기 0xC2)
/// </summary>
public enum PdCommand : byte
{
    WhoAmI = 0x01,
    OutputEnable = 0x02,
    OutputId = 0x03,
    OutputData = 0x04,

    /// <summary>실측 전압/전류. 읽기 전용(0x85).</summary>
    OutputDisplay = 0x05,

    OutputOcpEnable = 0x06,
    OutputOffsetEnable = 0x07,
    Brightness = 0x08,
    OutputDischargeEnable = 0x09,

    /// <summary>PD 입력 전압 요청. 프레임 종단 바이트(0x0A)와 값이 같으나 선두 위치라 모호하지 않다.</summary>
    InputState = 0x0A,

    SystemReset = 0x40,
    SystemUpgrade = 0x41,
    SystemVersion = 0x42,
    SystemSerialNumber = 0x43,
    SystemConfigSave = 0x44,
    SystemFactoryReset = 0x45,
    SystemLcdPanelType = 0x46,
    SystemFactoryData = 0x47,
}
