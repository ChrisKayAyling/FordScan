using System.Globalization;
using FordDiag.Comms;

namespace FordDiag.Comms.Diagnostics;

/// <summary>One diagnostic trouble code with its status byte.</summary>
/// <param name="Raw">2 bytes (KWP) or 3 bytes (UDS, last byte = failure type) as a big-endian number.</param>
/// <param name="Length">Number of bytes in the code (2 or 3).</param>
public readonly record struct Dtc(uint Raw, int Length, byte Status)
{
    /// <summary>SAE J2012 style text for the first two bytes, e.g. "P0123"; UDS failure-type byte appended as "-xx".</summary>
    public string Text
    {
        get
        {
            uint two = Length == 3 ? Raw >> 8 : Raw;
            char sys = ((two >> 14) & 3) switch { 0 => 'P', 1 => 'C', 2 => 'B', _ => 'U' };
            string s = $"{sys}{(two >> 12) & 3}{(two >> 8) & 0xF:X}{(two >> 4) & 0xF:X}{two & 0xF:X}";
            return Length == 3 ? s + "-" + (Raw & 0xFF).ToString("X2", CultureInfo.InvariantCulture) : s;
        }
    }

    public override string ToString() => $"{Text} [status 0x{Status:X2}]";
}

/// <summary>Convenience services (session, ReadDataByIdentifier, DTC read/clear) on top of any <see cref="IEcuTransport"/>.</summary>
public static class DiagnosticExtensions
{
    /// <summary>Sends a request and returns the positive response; a negative response throws <see cref="NegativeResponseException"/>.</summary>
    public static async ValueTask<byte[]> RequestPositiveAsync(this IEcuTransport transport, ReadOnlyMemory<byte> request, CancellationToken ct = default)
    {
        var rsp = await transport.RequestAsync(request, ct).ConfigureAwait(false);
        if (rsp.Length == 0) throw new ProtocolException("Empty response.");
        if (rsp[0] == 0x7F)
            throw new NegativeResponseException(rsp.Length > 1 ? rsp[1] : request.Span[0], rsp.Length > 2 ? rsp[2] : (byte)0);
        if (rsp[0] != request.Span[0] + 0x40)
            throw new ProtocolException($"Unexpected response 0x{rsp[0]:X2} to service 0x{request.Span[0]:X2}.");
        return rsp;
    }

    /// <summary>Hex-string overload ("10 C0").</summary>
    public static ValueTask<byte[]> RequestPositiveAsync(this IEcuTransport transport, string requestHex, CancellationToken ct = default) =>
        transport.RequestPositiveAsync(Hex.Parse(requestHex), ct);

    /// <summary>Sends a hex request and returns the response as spaced hex text.</summary>
    public static async ValueTask<string> RequestHexAsync(this IEcuTransport transport, string requestHex, CancellationToken ct = default) =>
        Hex.ToString(await transport.RequestAsync(Hex.Parse(requestHex), ct).ConfigureAwait(false), spaced: true);

    /// <summary>DiagnosticSessionControl (0x10). Returns true on a positive response, false on a negative one.</summary>
    public static async ValueTask<bool> StartSessionAsync(this IEcuTransport transport, byte session, CancellationToken ct = default)
    {
        var rsp = await transport.RequestAsync(new byte[] { 0x10, session }, ct).ConfigureAwait(false);
        return rsp.Length > 0 && rsp[0] == 0x50;
    }

    /// <summary>TesterPresent (0x3E 0x00).</summary>
    public static async ValueTask<bool> TesterPresentAsync(this IEcuTransport transport, CancellationToken ct = default)
    {
        var rsp = await transport.RequestAsync(new byte[] { 0x3E, 0x00 }, ct).ConfigureAwait(false);
        return rsp.Length > 0 && rsp[0] == 0x7E;
    }

    /// <summary>UDS ReadDataByIdentifier (0x22). Returns the data after "62 DID".</summary>
    public static async ValueTask<byte[]> ReadDataByIdentifierAsync(this IEcuTransport transport, ushort did, CancellationToken ct = default)
    {
        var rsp = await transport.RequestPositiveAsync(new byte[] { 0x22, (byte)(did >> 8), (byte)did }, ct).ConfigureAwait(false);
        if (rsp.Length < 3 || rsp[1] != (byte)(did >> 8) || rsp[2] != (byte)did)
            throw new ProtocolException("ReadDataByIdentifier response does not echo the identifier.");
        return rsp.AsSpan(3).ToArray();
    }

    /// <summary>KWP2000 ReadDataByLocalIdentifier (0x21). Returns the data after "61 id".</summary>
    public static async ValueTask<byte[]> ReadDataByLocalIdentifierAsync(this IEcuTransport transport, byte id, CancellationToken ct = default)
    {
        var rsp = await transport.RequestPositiveAsync(new byte[] { 0x21, id }, ct).ConfigureAwait(false);
        return rsp.Length >= 2 ? rsp.AsSpan(2).ToArray() : Array.Empty<byte>();
    }

    /// <summary>UDS ReadDTCInformation / reportDTCByStatusMask (19 02 mask). 0xAF requests confirmed, pending, test-failed and warning-indicator codes.</summary>
    public static async ValueTask<IReadOnlyList<Dtc>> ReadDtcsUdsAsync(this IEcuTransport transport, byte statusMask = 0xAF, CancellationToken ct = default)
    {
        var rsp = await transport.RequestPositiveAsync(new byte[] { 0x19, 0x02, statusMask }, ct).ConfigureAwait(false);
        var list = new List<Dtc>();
        // 59 02 availabilityMask { dtcHi dtcMid dtcLo status }*
        for (int i = 3; i + 3 < rsp.Length; i += 4)
            list.Add(new Dtc((uint)(rsp[i] << 16 | rsp[i + 1] << 8 | rsp[i + 2]), 3, rsp[i + 3]));
        return list;
    }

    /// <summary>KWP2000 ReadDiagnosticTroubleCodesByStatus (18 00 FF 00 = all codes, all groups).</summary>
    public static async ValueTask<IReadOnlyList<Dtc>> ReadDtcsKwpAsync(this IEcuTransport transport, ushort group = 0xFF00, CancellationToken ct = default)
    {
        var rsp = await transport.RequestPositiveAsync(new byte[] { 0x18, 0x00, (byte)(group >> 8), (byte)group }, ct).ConfigureAwait(false);
        var list = new List<Dtc>();
        // 58 count { dtcHi dtcLo status }*
        int count = rsp.Length > 1 ? rsp[1] : 0;
        for (int i = 2, n = 0; n < count && i + 2 < rsp.Length; i += 3, n++)
            list.Add(new Dtc((uint)(rsp[i] << 8 | rsp[i + 1]), 2, rsp[i + 2]));
        return list;
    }

    /// <summary>UDS ClearDiagnosticInformation (14 FF FF FF).</summary>
    public static async ValueTask ClearDtcsUdsAsync(this IEcuTransport transport, uint group = 0xFFFFFF, CancellationToken ct = default) =>
        await transport.RequestPositiveAsync(new byte[] { 0x14, (byte)(group >> 16), (byte)(group >> 8), (byte)group }, ct).ConfigureAwait(false);

    /// <summary>KWP2000 ClearDiagnosticInformation (14 FF 00).</summary>
    public static async ValueTask ClearDtcsKwpAsync(this IEcuTransport transport, ushort group = 0xFF00, CancellationToken ct = default) =>
        await transport.RequestPositiveAsync(new byte[] { 0x14, (byte)(group >> 8), (byte)group }, ct).ConfigureAwait(false);

    /// <summary>ECUReset (0x11), default hard reset.</summary>
    public static async ValueTask EcuResetAsync(this IEcuTransport transport, byte type = 0x01, CancellationToken ct = default) =>
        await transport.RequestPositiveAsync(new byte[] { 0x11, type }, ct).ConfigureAwait(false);

    /// <summary>Splits a response into (service, nrc) when it is a negative response.</summary>
    public static bool TryGetNegativeResponse(ReadOnlySpan<byte> response, out byte service, out byte nrc)
    {
        if (response.Length >= 3 && response[0] == 0x7F) { service = response[1]; nrc = response[2]; return true; }
        service = 0; nrc = 0; return false;
    }
}
