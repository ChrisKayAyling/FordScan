using FordDiag.Comms;

namespace FordDiag.Core;

/// <summary>The two OBD-II CAN networks Ford vehicles expose. HS-CAN: pins 6/14, 500 kbit/s. MS-CAN: pins 3/11, 125 kbit/s.</summary>
public enum FordBus { HsCan, MsCan }

/// <summary>How well an entry in <see cref="FordModules"/> is backed by sources.</summary>
public enum Confidence
{
    /// <summary>SAE J1979 / ISO 15765-4 defined (7E0/7E8...).</summary>
    Standard,
    /// <summary>Seen in Ford as-built documents or open-source projects for specific models.</summary>
    Documented,
    /// <summary>Common community knowledge; verify on your vehicle.</summary>
    Community,
}

public static class FordBusExtensions
{
    public static CanSpeed Speed(this FordBus bus) => bus == FordBus.MsCan ? CanSpeed.Kbps125 : CanSpeed.Kbps500;
    public static string Label(this FordBus bus) => bus == FordBus.MsCan ? "MS-CAN (125k, pins 3/11)" : "HS-CAN (500k, pins 6/14)";
}

/// <summary>A Ford control module reachable by CAN request id; the response id is request + 8.</summary>
public sealed record FordModule(string Abbrev, string Name, uint RequestId, FordBus TypicalBus, Confidence Confidence)
{
    public uint ResponseId => RequestId + 8;

    /// <summary>Which bus a given module sits on varies by model/year, so the bus is a parameter.</summary>
    public EcuAddress ToAddress(FordBus bus) => new()
    {
        Name = Abbrev, Protocol = EcuProtocol.Can, TxId = RequestId, RxId = ResponseId, Speed = bus.Speed(),
    };

    public override string ToString() => $"{Abbrev} ({RequestId:X3}/{ResponseId:X3}) {Name}";
}

public static class FordModules
{
    // Request ids: PCM/TCM per ISO 15765-4; BCM 726 and PCM 7E0 appear in the openRS_ Focus RS project;
    // ABS 760, IPC 720 and the 726-xx-xx style addresses come from Ford as-built documents / FORScan docs;
    // AWD 703, GFM 7D2 from openRS_. APIM 7D0 is as reported by community sources. Bus assignment differs per model/year.
    public static IReadOnlyList<FordModule> All { get; } = new FordModule[]
    {
        new("PCM", "Powertrain Control Module", 0x7E0, FordBus.HsCan, Confidence.Standard),
        new("TCM", "Transmission Control Module", 0x7E1, FordBus.HsCan, Confidence.Standard),
        new("ABS", "Anti-lock Brake / Stability Control", 0x760, FordBus.HsCan, Confidence.Documented),
        new("AWD", "All-Wheel Drive / Rear Drive Module", 0x703, FordBus.HsCan, Confidence.Documented),
        new("GFM", "Gateway / Front Module", 0x7D2, FordBus.HsCan, Confidence.Documented),
        new("BCM", "Body Control Module / Smart Junction Box", 0x726, FordBus.MsCan, Confidence.Documented),
        new("IPC", "Instrument Panel Cluster", 0x720, FordBus.MsCan, Confidence.Documented),
        new("APIM", "Accessory Protocol Interface Module (SYNC)", 0x7D0, FordBus.MsCan, Confidence.Community),
    };

    public static FordModule? Find(string abbrevOrHex)
    {
        var m = All.FirstOrDefault(x => x.Abbrev.Equals(abbrevOrHex, StringComparison.OrdinalIgnoreCase));
        if (m is not null) return m;
        return TryParseAddress(abbrevOrHex, out var id) ? All.FirstOrDefault(x => x.RequestId == id) ?? Unknown(id) : null;
    }

    public static FordModule Unknown(uint requestId) =>
        new($"{requestId:X3}", $"Unknown module {requestId:X3}", requestId, FordBus.HsCan, Confidence.Community);

    public static bool TryParseAddress(string text, out uint id)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        id = 0;
        return text.Length == 3 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out id) && id is >= 0x700 and <= 0x7FF;
    }
}
