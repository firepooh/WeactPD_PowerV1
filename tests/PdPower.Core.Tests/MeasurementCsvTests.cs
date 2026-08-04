using System.Globalization;
using PdPower.Core.Models;
using Xunit;

namespace PdPower.Core.Tests;

public class MeasurementCsvTests
{
    [Fact]
    public void WritesHeaderAndInvariantRows()
    {
        var t = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var window = new MeasurementWindow(
            [new MeasurementSample(t, 12.0005, 0.5, OutputRegulation.ConstantCurrent, true)],
            t, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(250));

        using var writer = new StringWriter();
        MeasurementCsv.Write(writer, window);
        var lines = writer.ToString().TrimEnd().Split(Environment.NewLine);

        Assert.Equal(MeasurementCsv.Header, lines[0]);
        Assert.Equal(2, lines.Length);
        var cols = lines[1].Split(',');
        Assert.Equal(t.ToString("O", CultureInfo.InvariantCulture), cols[0]);
        Assert.Equal("12.001", cols[1]);            // F3 반올림, 소수점은 항상 '.'
        Assert.Equal("0.500", cols[2]);
        Assert.Equal("6.000", cols[3]);
        Assert.Equal("ConstantCurrent", cols[4]);
        Assert.Equal("1", cols[5]);
    }

    [Fact]
    public void EmptyWindowWritesHeaderOnly()
    {
        using var writer = new StringWriter();
        MeasurementCsv.Write(writer, MeasurementWindow.Empty);
        Assert.Equal(MeasurementCsv.Header, writer.ToString().TrimEnd());
    }
}
