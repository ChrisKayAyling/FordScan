using System.Globalization;

namespace FordDiag.Comms;

/// <summary>Diagnostic protocol / bus an ECU is reached over.</summary>
public enum EcuProtocol
{
    /// <summary>ISO 15765 CAN (ISO-TP). 11 or 29 bit identifiers.</summary>
    Can,
    /// <summary>KWP2000 (ISO 14230) with fast initialisation.</summary>
    Kwp2000Fast,
    /// <summary>KWP2000 (ISO 14230) with 5-baud slow initialisation.</summary>
    Kwp2000Slow,
    /// <summary>ISO 9141-2 / ISO8 5-baud initialisation.</summary>
    Iso8,
}

/// <summary>CAN bit rate. <see cref="Default"/> means 500k unless the ECU description says otherwise.</summary>
public enum CanSpeed
{
    Default = 0,
    Kbps500 = 1,
    Kbps250 = 2,
    /// <summary>Try 250k first and fall back to 500k when the adapter reports CAN ERROR.</summary>
    Auto = 3,
    /// <summary>125k: Ford MS-CAN (OBD pins 3/11). Needs an adapter that can reach those pins (hardware switch, vLinker FS, OBDLink EX).</summary>
    Kbps125 = 4,
}

/// <summary>
/// Everything a transport needs to talk to one ECU. Independent of any Core type.
/// For CAN, <see cref="TxId"/> is the identifier the tester transmits on and <see cref="RxId"/> the ECU's reply identifier.
/// For KWP/ISO8, <see cref="FuncAddress"/> is the ECU's functional (K-line) address.
/// </summary>
public sealed record EcuAddress
{
    public string Name { get; init; } = "";
    public EcuProtocol Protocol { get; init; } = EcuProtocol.Can;
    public uint TxId { get; init; }
    public uint RxId { get; init; }
    /// <summary>True for 29-bit identifiers (inferred from 8 hex digits by <see cref="Can(string,string,string,CanSpeed)"/>).</summary>
    public bool Is29Bit { get; init; }
    /// <summary>ISO-TP extended addressing byte (ELM "ATCEA hh"), if the ECU uses it.</summary>
    public byte? ExtAddress { get; init; }
    public byte? FuncAddress { get; init; }
    public CanSpeed Speed { get; init; }
    /// <summary>Optional diagnostic-session request (e.g. 10 C0) sent after addressing; re-sent after keep-alive silence.</summary>
    public ReadOnlyMemory<byte> StartSession { get; init; }

    /// <summary>Builds a CAN address from hex strings such as "7E0"/"7E8" or "18DA10F1"/"18DAF110".</summary>
    public static EcuAddress Can(string name, string txHex, string rxHex, CanSpeed speed = CanSpeed.Default)
    {
        txHex = txHex.Trim(); rxHex = rxHex.Trim();
        return new EcuAddress
        {
            Name = name,
            Protocol = EcuProtocol.Can,
            TxId = uint.Parse(txHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            RxId = uint.Parse(rxHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            Is29Bit = txHex.Length > 3 || rxHex.Length > 3,
            Speed = speed,
        };
    }

    /// <summary>Builds a K-line (KWP2000/ISO8) address from the ECU functional address, e.g. "7A".</summary>
    public static EcuAddress KLine(string name, EcuProtocol protocol, string funcAddrHex) => new()
    {
        Name = name,
        Protocol = protocol,
        FuncAddress = byte.Parse(funcAddrHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
    };

    /// <summary>Hex text of TX id (3 or 8 digits).</summary>
    public string TxHex => Is29Bit ? TxId.ToString("X8") : TxId.ToString("X3");
    public string RxHex => Is29Bit ? RxId.ToString("X8") : RxId.ToString("X3");
}
