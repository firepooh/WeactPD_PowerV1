using PdPower.Core;
using Xunit;

namespace PdPower.Core.Tests;

public class PortPreferenceTests
{
    [Fact]
    public void KeepsCurrentSelectionWhenStillAvailable()
        => Assert.Equal("COM9", PortPreference.Choose(["COM3", "COM9"], "COM9", "COM3"));

    [Fact]
    public void FallsBackToLastConnectedPort()
        => Assert.Equal("COM9", PortPreference.Choose(["COM3", "COM9"], null, "COM9"));

    [Fact]
    public void LastConnectedBeatsAlphabeticalOrder()
        => Assert.Equal("COM9", PortPreference.Choose(["COM3", "COM9"], "COM7", "COM9"));

    [Fact]
    public void FallsBackToFirstAvailable()
        => Assert.Equal("COM3", PortPreference.Choose(["COM3", "COM9"], null, "COM5"));

    [Fact]
    public void ComparesCaseInsensitively()
        => Assert.Equal("com9", PortPreference.Choose(["COM3", "com9"], null, "COM9"));

    [Fact]
    public void ReturnsNullWhenNoPorts()
        => Assert.Null(PortPreference.Choose([], "COM9", "COM9"));
}
