namespace PdPower.Core.Protocol;

/// <summary>
/// 프레임 인코딩/디코딩. 두 모드의 차이는 마지막 바이트(0x0A vs CRC-8)와
/// ASCII 응답의 길이 바이트 유무뿐이므로 한 곳에서 흡수한다.
/// </summary>
public static class Frame
{
    /// <summary>읽기 요청을 나타내는 명령 바이트 비트.</summary>
    public const byte ReadMask = 0x80;

    /// <summary>USB CDC 모드의 프레임 종단 바이트.</summary>
    public const byte EndByte = 0x0A;

    /// <summary>쓰기 프레임 생성: [cmd][payload...][end/crc]</summary>
    public static byte[] BuildWrite(ProtocolMode mode, PdCommand command, params byte[] payload)
        => Build(mode, (byte)command, payload);

    /// <summary>읽기 요청 프레임 생성: [cmd|0x80][payload...][end/crc]</summary>
    public static byte[] BuildRead(ProtocolMode mode, PdCommand command, params byte[] payload)
        => Build(mode, (byte)((byte)command | ReadMask), payload);

    private static byte[] Build(ProtocolMode mode, byte head, ReadOnlySpan<byte> payload)
    {
        var frame = new byte[payload.Length + 2];
        frame[0] = head;
        payload.CopyTo(frame.AsSpan(1));
        frame[^1] = mode == ProtocolMode.Uart
            ? Crc8.Compute(frame.AsSpan(0, frame.Length - 1))
            : EndByte;
        return frame;
    }

    /// <summary>
    /// 읽기 응답의 전체 프레임 길이(명령 에코 + 페이로드 + 종단 바이트).
    /// ASCII 가변 길이 응답은 <c>null</c>.
    /// </summary>
    public static int? ResponseLength(PdCommand command) => command switch
    {
        PdCommand.OutputEnable => 3,
        PdCommand.OutputId => 3,
        PdCommand.OutputData => 7,
        PdCommand.OutputDisplay => 6,
        PdCommand.OutputOcpEnable => 3,
        PdCommand.OutputOffsetEnable => 3,
        PdCommand.Brightness => 3,
        PdCommand.InputState => 7,
        PdCommand.SystemLcdPanelType => 3,
        PdCommand.SystemFactoryData => 66,

        // 가변 길이 ASCII 응답
        PdCommand.WhoAmI => null,
        PdCommand.SystemVersion => null,
        PdCommand.SystemSerialNumber => null,

        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "읽기를 지원하지 않는 명령입니다."),
    };

    /// <summary>
    /// 고정 길이 응답에서 페이로드만 떼어낸다. 명령 에코와 CRC(UART 모드)를 검증한다.
    /// </summary>
    public static ReadOnlySpan<byte> ExtractPayload(ProtocolMode mode, PdCommand command, ReadOnlySpan<byte> frame)
    {
        byte expectedHead = (byte)((byte)command | ReadMask);
        if (frame.Length < 2 || frame[0] != expectedHead)
            throw new PdPowerProtocolException(
                $"응답 헤더 불일치: 0x{expectedHead:X2} 기대, 0x{(frame.Length > 0 ? frame[0] : 0):X2} 수신");

        if (mode == ProtocolMode.Uart)
        {
            byte expected = Crc8.Compute(frame[..^1]);
            if (frame[^1] != expected)
                throw new PdPowerProtocolException(
                    $"CRC8 불일치: 0x{expected:X2} 기대, 0x{frame[^1]:X2} 수신");
        }

        return frame[1..^1];
    }

    /// <summary>리틀 엔디언 16비트 값 읽기.</summary>
    public static ushort ReadUInt16(ReadOnlySpan<byte> payload, int offset)
        => (ushort)(payload[offset] | (payload[offset + 1] << 8));

    /// <summary>리틀 엔디언 16비트 값을 2바이트 배열로.</summary>
    public static byte[] WriteUInt16(int value)
        => [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)];
}
