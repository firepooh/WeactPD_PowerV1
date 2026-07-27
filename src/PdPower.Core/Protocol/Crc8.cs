namespace PdPower.Core.Protocol;

/// <summary>
/// UART 모드 프레임 검사용 CRC-8.
/// 다항식 0x31 (x^8 + x^5 + x^4 + 1), 초기값 0xFF, MSB-first 비트 단위 처리.
/// </summary>
public static class Crc8
{
    private const byte Polynomial = 0x31;
    private const byte InitialValue = 0xFF;

    public static byte Compute(ReadOnlySpan<byte> data)
    {
        byte crc = InitialValue;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ Polynomial) : (byte)(crc << 1);
        }
        return crc;
    }
}
