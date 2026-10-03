using FordDiag.Comms;

namespace FordDiag.Core.Capture;

/// <summary>Plain-language descriptions of UDS requests and answers.</summary>
public static class UdsAnnotator
{
    public static string ServiceName(byte sid) => sid switch
    {
        0x10 => "Diagnostic session", 0x11 => "ECU reset", 0x14 => "Clear DTCs", 0x19 => "Read DTCs", 0x22 => "Read DID", 0x23 => "Read memory",
        0x27 => "Security access", 0x28 => "Communication control", 0x2A => "Periodic read", 0x2C => "Define data identifier", 0x2E => "Write DID",
        0x2F => "I/O control", 0x31 => "Routine control", 0x34 => "Request download", 0x35 => "Request upload", 0x36 => "Transfer data", 0x37 => "Transfer exit",
        0x3D => "Write memory", 0x3E => "Tester present", 0x85 => "Control DTC setting", 0x01 => "OBD mode 01", 0x02 => "OBD freeze frame", 0x03 => "OBD stored DTCs",
        0x04 => "OBD clear DTCs", 0x08 => "OBD test control", 0x09 => "OBD vehicle info", 0x0A => "OBD permanent DTCs", _ => $"Service 0x{sid:X2}",
    };

    public static string Describe(UdsExchange x)
    {
        var r = x.Request;
        if (r.Length == 0) return "(empty request)";
        string what = r[0] switch
        {
            0x10 when r.Length > 1 => $"Session: {r[1] switch { 1 => "default", 2 => "programming", 3 => "extended", _ => "0x" + r[1].ToString("X2") }}",
            0x11 when r.Length > 1 => $"Reset: {r[1] switch { 1 => "hard", 2 => "key off/on", 3 => "soft", _ => "0x" + r[1].ToString("X2") }}",
            0x14 => "Clear DTCs",
            0x19 when r.Length > 1 => $"Read DTCs (sub 0x{r[1]:X2})",
            0x22 when r.Length >= 3 => $"Read DID {r[1]:X2}{r[2]:X2}" + (Name(r[1] << 8 | r[2]) is { } n ? $" ({n})" : ""),
            0x2E when r.Length >= 3 => $"WRITE DID {r[1]:X2}{r[2]:X2} = {Hex.ToString(r.AsSpan(3), true)}",
            0x27 when r.Length > 1 => r[1] % 2 == 1 ? $"Security access: request seed (level 0x{r[1]:X2})" : $"Security access: send key (level 0x{r[1] - 1:X2})",
            0x2F when r.Length >= 4 => $"I/O control DID {r[1]:X2}{r[2]:X2}: {r[3] switch { 0 => "return control to module", 1 => "reset to default", 2 => "freeze", 3 => "adjust to " + Hex.ToString(r.AsSpan(4), true), _ => "option 0x" + r[3].ToString("X2") }}",
            0x31 when r.Length >= 4 => $"Routine {r[2]:X2}{r[3]:X2}: {r[1] switch { 1 => "start", 2 => "stop", 3 => "results", _ => "sub 0x" + r[1].ToString("X2") }}" + (r.Length > 4 ? " data " + Hex.ToString(r.AsSpan(4), true) : ""),
            0x3E => "Tester present",
            0x85 when r.Length > 1 => r[1] == 1 ? "DTC setting ON" : r[1] == 2 ? "DTC setting OFF" : "DTC setting",
            0x28 when r.Length > 1 => "Communication control 0x" + r[1].ToString("X2"),
            0x34 or 0x36 or 0x37 => "Programming transfer (" + ServiceName(r[0]) + ")",
            _ => ServiceName(r[0]) + (r.Length > 1 ? " " + Hex.ToString(r.AsSpan(1), true) : ""),
        };
        if (x.Error is not null) return what + " → " + x.Error;
        if (x.Response is null) return what + " → no answer";
        if (x.Negative) return what + $" → refused: {NegativeResponses.Describe(x.Response[2])}";
        return what + " → OK";
    }

    private static string? Name(int did) => FordDids.Identification.FirstOrDefault(d => d.Did == did).Name;

    public static bool IsSecurity(UdsExchange x) => x.Service == 0x27;
    public static bool IsProgramming(UdsExchange x) => x.Service is 0x34 or 0x36 or 0x37 || x.Service == 0x10 && x.Request.Length > 1 && x.Request[1] == 0x02;
    public static bool IsWrite(UdsExchange x) => x.Service is 0x2E or 0x2F or 0x31 or 0x11 or 0x14 or 0x3D;
}
