using FordDiag.Comms.Elm;

namespace FordDiag.Core;

/// <summary>How an adapter reaches the MS-CAN pair (OBD pins 3/11).</summary>
public enum BusSwitching
{
    /// <summary>No MS-CAN access (plain ELM327 without modification): HS-CAN only.</summary>
    None,
    /// <summary>A physical HS/MS switch the user must flip (modified ELM327; FORScan prompts for this).</summary>
    Manual,
    /// <summary>The adapter routes MS-CAN itself when the MS protocol is selected (vLinker FS, OBDLink EX).</summary>
    Automatic,
}

public sealed record FordAdapter(string Key, string DisplayName, ElmProfile Profile, BusSwitching Switching, string Notes);

public static class FordAdapters
{
    public static IReadOnlyList<FordAdapter> All { get; } = new FordAdapter[]
    {
        new("vlinker-fs", "vLinker FS USB (HS/MS auto)", ElmProfiles.VLinker, BusSwitching.Automatic,
            "Update to current firmware first (v2.2.78+ for pre-2005 vehicles). USB is an FTDI virtual COM port; use 115200 baud."),
        new("obdlink-ex", "OBDLink EX USB (STN2120)", ElmProfiles.ObdLinkEx, BusSwitching.Automatic,
            "Recommended by FORScan. MS-CAN is selected with STN protocol preset 53."),
        new("elm327-switch", "ELM327 with HS/MS switch", ElmProfiles.Elm327Clone, BusSwitching.Manual,
            "Genuine-firmware v1.5 clones often corrupt data above 38400 baud; the user flips the switch when prompted."),
        new("elm327", "ELM327 (no MS-CAN)", ElmProfiles.Elm327Clone, BusSwitching.None, "HS-CAN modules only."),
    };

    public static FordAdapter? Find(string key) => All.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
