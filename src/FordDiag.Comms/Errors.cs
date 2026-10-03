namespace FordDiag.Comms;

/// <summary>Base class of all communication failures raised by this library.</summary>
public class CommsException : Exception
{
    public CommsException(string message) : base(message) { }
    public CommsException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>The adapter or ECU did not answer in time (ELM "NO DATA", prompt timeout, adapter timeout...).</summary>
public sealed class EcuTimeoutException : CommsException
{
    public EcuTimeoutException(string message) : base(message) { }
}

/// <summary>The ELM-family adapter reported an error such as "CAN ERROR", "BUS INIT: ERROR", "?" or "BUFFER FULL".</summary>
public sealed class ElmErrorException : CommsException
{
    /// <summary>The raw adapter message (upper-case).</summary>
    public string ElmMessage { get; }
    public ElmErrorException(string elmMessage, string? context = null)
        : base(context is null ? $"ELM error: {elmMessage}" : $"ELM error during {context}: {elmMessage}")
        => ElmMessage = elmMessage;
}

/// <summary>Thrown by the helper methods when an ECU answers with a negative response (7F sid nrc).</summary>
public sealed class NegativeResponseException : CommsException
{
    public byte Service { get; }
    public byte Code { get; }
    public NegativeResponseException(byte service, byte code)
        : base($"Negative response to service 0x{service:X2}: 0x{code:X2} ({NegativeResponses.Describe(code)})")
    {
        Service = service;
        Code = code;
    }
}

/// <summary>Malformed ISO-TP / ELM data or unsupported configuration.</summary>
public sealed class ProtocolException : CommsException
{
    public ProtocolException(string message) : base(message) { }
}

/// <summary>The link (serial/TCP) was closed or the device went away.</summary>
public sealed class LinkClosedException : CommsException
{
    public LinkClosedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Diagnostic negative response codes (ISO 14229 / KWP2000).</summary>
public static class NegativeResponses
{
    private static readonly Dictionary<byte, string> Table = new()
    {
        [0x10] = "General Reject",
        [0x11] = "Service Not Supported",
        [0x12] = "SubFunction Not Supported",
        [0x13] = "Incorrect Message Length Or Invalid Format",
        [0x21] = "Busy Repeat Request",
        [0x22] = "Conditions Not Correct Or Request Sequence Error",
        [0x23] = "Routine Not Complete",
        [0x24] = "Request Sequence Error",
        [0x31] = "Request Out Of Range",
        [0x33] = "Security Access Denied- Security Access Requested",
        [0x35] = "Invalid Key",
        [0x36] = "Exceed Number Of Attempts",
        [0x37] = "Required Time Delay Not Expired",
        [0x40] = "Download not accepted",
        [0x41] = "Improper download type",
        [0x42] = "Can not download to specified address",
        [0x43] = "Can not download number of bytes requested",
        [0x50] = "Upload not accepted",
        [0x51] = "Improper upload type",
        [0x52] = "Can not upload from specified address",
        [0x53] = "Can not upload number of bytes requested",
        [0x70] = "Upload Download NotAccepted",
        [0x71] = "Transfer Data Suspended",
        [0x72] = "General Programming Failure",
        [0x73] = "Wrong Block Sequence Counter",
        [0x74] = "Illegal Address In Block Transfer",
        [0x75] = "Illegal Byte Count In Block Transfer",
        [0x76] = "Illegal Block Transfer Type",
        [0x77] = "Block Transfer Data Checksum Error",
        [0x78] = "Request Correctly Received-Response Pending",
        [0x79] = "Incorrect ByteCount During Block Transfer",
        [0x7E] = "SubFunction Not Supported In Active Session",
        [0x7F] = "Service Not Supported In Active Session",
        [0x80] = "Service Not Supported In Active Diagnostic Mode",
        [0x81] = "Rpm Too High",
        [0x82] = "Rpm Too Low",
        [0x83] = "Engine Is Running",
        [0x84] = "Engine Is Not Running",
        [0x85] = "Engine RunTime TooLow",
        [0x86] = "Temperature Too High",
        [0x87] = "Temperature Too Low",
        [0x88] = "Vehicle Speed Too High",
        [0x89] = "Vehicle Speed Too Low",
        [0x8A] = "Throttle/Pedal Too High",
        [0x8B] = "Throttle/Pedal Too Low",
        [0x8C] = "Transmission Range In Neutral",
        [0x8D] = "Transmission Range In Gear",
        [0x8F] = "Brake Switch(es)NotClosed (brake pedal not pressed or not applied)",
        [0x90] = "Shifter Lever Not In Park",
        [0x91] = "Torque Converter Clutch Locked",
        [0x92] = "Voltage Too High",
        [0x93] = "Voltage Too Low",
    };

    /// <summary>Human readable text for a negative response code.</summary>
    public static string Describe(byte code) => Table.TryGetValue(code, out var s) ? s : "Unregistered error";

    /// <summary>True for NRC 0x78 (request correctly received, response pending).</summary>
    public static bool IsPending(byte code) => code == 0x78;
}
