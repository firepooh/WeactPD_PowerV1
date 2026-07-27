using PdPower.Core;
using PdPower.Core.Protocol;

namespace PdPower.Core.Tests;

public class FrameTests
{
    [Fact]
    public void USB모드_읽기요청은_0x80을_OR하고_0x0A로_끝난다()
    {
        Assert.Equal([0x85, 0x0A], Frame.BuildRead(ProtocolMode.UsbCdc, PdCommand.OutputDisplay));
    }

    [Fact]
    public void UART모드_읽기요청은_0x80을_OR하고_CRC로_끝난다()
    {
        // 문서상 READ_OUTPUT_DISPLAY = 0x85, crc8(0x23)
        Assert.Equal([0x85, 0x23], Frame.BuildRead(ProtocolMode.Uart, PdCommand.OutputDisplay));
    }

    [Fact]
    public void OUTPUT_DATA_쓰기프레임은_리틀엔디언_7바이트다()
    {
        // M0에 5.000 V / 1.500 A → 5000 mV = 0x1388, 1500 mA = 0x05DC
        var frame = Frame.BuildWrite(ProtocolMode.UsbCdc, PdCommand.OutputData,
            [0x00, .. Frame.WriteUInt16(5000), .. Frame.WriteUInt16(1500)]);

        Assert.Equal([0x04, 0x00, 0x88, 0x13, 0xDC, 0x05, 0x0A], frame);
    }

    [Fact]
    public void 문서의_응답길이표와_일치한다()
    {
        Assert.Equal(3, Frame.ResponseLength(PdCommand.OutputEnable));
        Assert.Equal(3, Frame.ResponseLength(PdCommand.OutputId));
        Assert.Equal(7, Frame.ResponseLength(PdCommand.OutputData));
        Assert.Equal(6, Frame.ResponseLength(PdCommand.OutputDisplay));
        Assert.Equal(7, Frame.ResponseLength(PdCommand.InputState));
        Assert.Equal(66, Frame.ResponseLength(PdCommand.SystemFactoryData));
        Assert.Null(Frame.ResponseLength(PdCommand.WhoAmI));
    }

    [Fact]
    public void 응답_페이로드에서_헤더와_종단바이트가_제거된다()
    {
        // READ_OUTPUT_DISPLAY 응답: 9.001 V / 0.000 A → 9001 mV = 0x2329
        byte[] response = [0x85, 0x29, 0x23, 0x00, 0x00, 0x0A];
        var payload = Frame.ExtractPayload(ProtocolMode.UsbCdc, PdCommand.OutputDisplay, response);

        Assert.Equal(4, payload.Length);
        Assert.Equal(9001, Frame.ReadUInt16(payload, 0));
        Assert.Equal(0, Frame.ReadUInt16(payload, 2));
    }

    [Fact]
    public void 헤더가_다른_응답은_예외를_던진다()
    {
        byte[] wrongHead = [0x84, 0x29, 0x23, 0x00, 0x00, 0x0A];
        Assert.Throws<PdPowerProtocolException>(
            () => Frame.ExtractPayload(ProtocolMode.UsbCdc, PdCommand.OutputDisplay, wrongHead).ToArray());
    }

    [Fact]
    public void UART모드에서_CRC가_틀린_응답은_예외를_던진다()
    {
        byte[] badCrc = [0x85, 0x29, 0x23, 0x00, 0x00, 0xFF];
        Assert.Throws<PdPowerProtocolException>(
            () => Frame.ExtractPayload(ProtocolMode.Uart, PdCommand.OutputDisplay, badCrc).ToArray());
    }

    [Theory]
    [InlineData(0, 0x00, 0x00)]
    [InlineData(20000, 0x20, 0x4E)]
    [InlineData(65535, 0xFF, 0xFF)]
    public void UInt16_변환은_왕복한다(int value, byte low, byte high)
    {
        var bytes = Frame.WriteUInt16(value);
        Assert.Equal([low, high], bytes);
        Assert.Equal(value, Frame.ReadUInt16(bytes, 0));
    }
}
