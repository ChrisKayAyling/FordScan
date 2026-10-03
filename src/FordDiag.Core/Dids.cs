namespace FordDiag.Core;

/// <summary>Identification DIDs read with UDS service 0x22. F1xx values are ISO 14229 standard; Ford uses most of them.</summary>
public static class FordDids
{
    public static IReadOnlyList<(ushort Did, string Name, bool Ascii)> Identification { get; } = new (ushort, string, bool)[]
    {
        (0xF190, "VIN", true),
        (0xF188, "Software / strategy part number", true),
        (0xF113, "Assembly part number", true),
        (0xF124, "Calibration part number", true),
        (0xF187, "Spare part number", true),
        (0xF18C, "ECU serial number", true),
    };

    public static string Format(byte[] data, bool ascii)
    {
        if (ascii && data.Length > 0 && data.All(b => b is >= 0x20 and < 0x7F)) return System.Text.Encoding.ASCII.GetString(data).Trim();
        return FordDiag.Comms.Hex.ToString(data, true);
    }
}
