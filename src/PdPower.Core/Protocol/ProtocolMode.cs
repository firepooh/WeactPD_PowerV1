namespace PdPower.Core.Protocol;

/// <summary>프레임 종단 방식. 장치의 물리 연결에 따라 결정된다.</summary>
public enum ProtocolMode
{
    /// <summary>USB CDC 가상 COM. 프레임 마지막 바이트가 0x0A 고정.</summary>
    UsbCdc,

    /// <summary>Type-C DP/DM 직결 UART. 프레임 마지막 바이트가 CRC-8, ASCII 응답에 길이 바이트가 붙는다.</summary>
    Uart,
}
