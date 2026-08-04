using System.Globalization;

namespace PdPower.Core.Models;

/// <summary>측정 창을 CSV 로 쓴다. 열: timestamp,volts,amps,watts,regulation,output_enabled.</summary>
public static class MeasurementCsv
{
    public const string Header = "timestamp,volts,amps,watts,regulation,output_enabled";

    public static void Write(TextWriter writer, MeasurementWindow window)
    {
        writer.WriteLine(Header);
        foreach (var s in window.Samples)
        {
            writer.WriteLine(string.Join(',',
                s.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                s.Volts.ToString("F3", CultureInfo.InvariantCulture),
                s.Amps.ToString("F3", CultureInfo.InvariantCulture),
                s.Watts.ToString("F3", CultureInfo.InvariantCulture),
                s.Regulation,
                s.OutputEnabled ? 1 : 0));
        }
    }
}
