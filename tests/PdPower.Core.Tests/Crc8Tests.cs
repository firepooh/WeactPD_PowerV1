using PdPower.Core.Protocol;

namespace PdPower.Core.Tests;

/// <summary>
/// 기대값은 제조사 UART 프로토콜 문서(v1.1)에 명시된 crc8(...) 주석에서 가져온 것이다.
/// 15개 명령의 값이 모두 맞으면 다항식/초기값/비트 처리 순서가 옳다고 볼 수 있다.
/// </summary>
public class Crc8Tests
{
    [Theory]
    // 쓰기 명령
    [InlineData(0x40, 0x91)] // SYSTEM_RESET
    [InlineData(0x44, 0x55)] // SYSTEM_CONFIG_SAVE
    [InlineData(0x45, 0x64)] // SYSTEM_FACTORY_RESET
    // 읽기 명령
    [InlineData(0x81, 0xE7)] // WHO_AM_I
    [InlineData(0x82, 0xB4)] // READ_OUTPUT_STATE
    [InlineData(0x83, 0x85)] // READ_OUTPUT_ID
    [InlineData(0x85, 0x23)] // READ_OUTPUT_DISPLAY
    [InlineData(0x86, 0x70)] // READ_OUTPUT_OCP_EN
    [InlineData(0x87, 0x41)] // READ_OUTPUT_OFFSET_EN
    [InlineData(0x88, 0x6F)] // READ_BRIGHTNESS
    [InlineData(0x8A, 0x0D)] // READ_INPUT_STATE
    [InlineData(0xC2, 0x89)] // READ_SYSTEM_VERSION
    [InlineData(0xC3, 0xB8)] // READ_SYSTEM_SERIAL_NUM
    [InlineData(0xC6, 0x4D)] // READ_SYSTEM_LCD_PANEL_TYPE
    [InlineData(0xC7, 0x7C)] // READ_SYSTEM_FACTORY_DATA
    public void 문서에_명시된_단일바이트_CRC와_일치한다(byte command, byte expected)
    {
        Assert.Equal(expected, Crc8.Compute([command]));
    }

    [Fact]
    public void 빈_입력은_초기값을_그대로_반환한다()
    {
        Assert.Equal(0xFF, Crc8.Compute([]));
    }
}
