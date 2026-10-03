namespace FordDiag.Comms.Elm;

/// <summary>How to raise the UART speed of an adapter after the initial handshake.</summary>
public enum SpeedSwitchMode
{
    /// <summary>Stay at the connect speed.</summary>
    None,
    /// <summary>ELM327 "ATBRD hh" divisor switch.</summary>
    AtBrd,
    /// <summary>STN-based adapters (OBDLink, VGate, VLinker): "ST SBR n".</summary>
    StSbr,
}

/// <summary>Per-adapter-family settings.</summary>
public sealed record ElmProfile
{
    /// <summary>Stable key, e.g. "vlinker", "obdlink_ex".</summary>
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public int DefaultBaud { get; init; } = 38400;
    /// <summary>Speeds the adapter can be switched to (after connecting).</summary>
    public IReadOnlyList<int> AvailableBauds { get; init; } = Array.Empty<int>();
    /// <summary>Speeds to try while negotiating the initial connection, in order.</summary>
    public IReadOnlyList<int> ProbeBauds { get; init; } = new[] { 38400, 115200, 230400, 57600, 9600, 500000, 1000000, 2000000 };
    /// <summary>Host-side wait for an adapter prompt, seconds.</summary>
    public double TimeoutSeconds { get; init; } = 5;
    public bool RtsCts { get; init; }
    public bool DsrDtr { get; init; }
    public string Category { get; init; } = "General";
    public string Notes { get; init; } = "";
    public SpeedSwitchMode SpeedSwitch { get; init; } = SpeedSwitchMode.AtBrd;
    /// <summary>True for STN11xx/STN21xx based adapters (supports ST* commands).</summary>
    public bool IsStn { get; init; }
    /// <summary>Extra AT commands sent after the standard init (adapter specific setup).</summary>
    public IReadOnlyList<string> PostInitCommands { get; init; } = Array.Empty<string>();

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}

/// <summary>Built-in device profiles matching the README "Supported devices" table.</summary>
public static class ElmProfiles
{
    private static readonly int[] StdSpeeds = { 38400 };

    public static ElmProfile VLinker { get; } = new()
    {
        Key = "vlinker", DisplayName = "VLinker FS", DefaultBaud = 38400, AvailableBauds = new[] { 57600, 115200 },
        TimeoutSeconds = 3, Category = "Enhanced", Notes = "Most stable, best compatibility", SpeedSwitch = SpeedSwitchMode.StSbr, IsStn = false,
    };

    public static ElmProfile VGate { get; } = new()
    {
        Key = "vgate", DisplayName = "VGate iCar Pro", DefaultBaud = 115200, AvailableBauds = new[] { 115200, 230400, 500000, 1000000 },
        ProbeBauds = new[] { 115200, 38400, 230400, 57600, 9600, 500000, 1000000, 2000000 },
        TimeoutSeconds = 2, Category = "Enhanced", Notes = "High-speed (up to 1 000 000 bps)", SpeedSwitch = SpeedSwitchMode.StSbr,
    };

    public static ElmProfile Elm327 { get; } = new()
    {
        Key = "elm327", DisplayName = "ELM327 (original)", DefaultBaud = 38400, AvailableBauds = new[] { 57600, 115200, 230400, 500000 },
        TimeoutSeconds = 5, Category = "General", Notes = "Usually marked PIC18F25K80",
    };

    public static ElmProfile Elm327Clone { get; } = new()
    {
        Key = "elm327_clone", DisplayName = "ELM327 (clone)", DefaultBaud = 38400, AvailableBauds = Array.Empty<int>(),
        ProbeBauds = new[] { 38400, 9600, 115200, 57600, 230400 },
        TimeoutSeconds = 5, Category = "Budget", Notes = "Try different baud settings", SpeedSwitch = SpeedSwitchMode.None,
    };

    public static ElmProfile Elm327Usb { get; } = new()
    {
        Key = "elm327_usb", DisplayName = "ELM327 USB", DefaultBaud = 38400, AvailableBauds = new[] { 115200 },
        TimeoutSeconds = 5, Category = "USB", Notes = "Dedicated ELM327 USB (STD_USB)",
    };

    public static ElmProfile ObdLinkSx { get; } = new()
    {
        Key = "obdlink", DisplayName = "OBDLink SX", DefaultBaud = 115200, AvailableBauds = new[] { 500000, 1000000, 2000000 },
        ProbeBauds = new[] { 115200, 38400, 230400, 57600, 9600, 500000, 1000000, 2000000 },
        TimeoutSeconds = 2, RtsCts = true, Category = "Professional", Notes = "Highest-speed adapter", SpeedSwitch = SpeedSwitchMode.StSbr, IsStn = true,
    };

    public static ElmProfile ObdLinkEx { get; } = ObdLinkSx with { Key = "obdlink_ex", DisplayName = "OBDLink EX", Notes = "Recommended by FORScan; has a real MS-CAN transceiver" };

    /// <summary>Fallback for unrecognised adapters.</summary>
    public static ElmProfile Unknown { get; } = Elm327 with { Key = "unknown", DisplayName = "Unknown ELM-compatible adapter", AvailableBauds = Array.Empty<int>() };

    /// <summary>All profiles in the order they should be offered to the user.</summary>
    public static IReadOnlyList<ElmProfile> All { get; } = new[]
    {
        VLinker, VGate, Elm327, Elm327Clone, Elm327Usb, ObdLinkSx, ObdLinkEx,
    };

    /// <summary>Preferred device order for Ford work.</summary>
    public static IReadOnlyList<string> PreferredOrder { get; } =
        new[] { "vlinker", "obdlink_ex", "obdlink", "vgate", "elm327" };

    public static ElmProfile? ByKey(string key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps an adapter type name (STD_BT, STD_WIFI, STD_USB, OBDLINK, OBDLINK_EX, VLINKER, VGATE) to a profile.</summary>
    public static ElmProfile FromAdapterType(string adapterType) => adapterType.Trim().ToUpperInvariant() switch
    {
        "STD_BT" or "STD_WIFI" => Elm327,
        "STD_USB" => Elm327Usb,
        "OBDLINK" => ObdLinkSx,
        "OBDLINK_EX" => ObdLinkEx,
        "VLINKER" => VLinker,
        "VGATE" => VGate,
        _ => ByKey(adapterType) ?? Elm327,
    };

    /// <summary>Port description heuristics.</summary>
    public static ElmProfile? GuessFromDescription(string description, int? vid = null, int? pid = null)
    {
        var d = description.ToUpperInvariant();
        if (d.Contains("VLINKER") || d.Contains("OBDII")) return VLinker;
        if (d.Contains("VGATE") || d.Contains("ICAR")) return VGate;
        if (d.Contains("OBDLINK") || d.Contains("SCANTOOL")) return ObdLinkSx;
        if (d.Contains("ELM327")) return Elm327;
        return (vid, pid) switch
        {
            (0x0403, 0x6015) => ObdLinkSx,
            (0x0403, 0x6001) or (0x1A86, 0x7523) or (0x10C4, 0xEA60) or (0x067B, 0x2303) => Elm327Usb,
            _ => null,
        };
    }

    /// <summary>
    /// Detects the family from the adapter's ATI / STI / AT@1 text.
    /// </summary>
    public static ElmProfile DetectFromVersion(string text)
    {
        var v = text.ToUpperInvariant();
        if (v.Contains("VGATE") || v.Contains("ICAR")) return VGate;
        if (v.Contains("OBDLINK") || v.Contains("STN")) return v.Contains("EX") ? ObdLinkEx : ObdLinkSx;
        if (v.Contains("VLINKER")) return VLinker;
        if (v.Contains("ELM327")) return Elm327;
        return Unknown;
    }
}
