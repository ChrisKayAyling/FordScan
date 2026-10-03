using FordDiag.Comms;

namespace FordDiag.Core.Live;

public static class PidReader
{
    /// <summary>Sends "22 DID" and returns the data after "62 DID". Throws on a negative response or an answer for another DID.</summary>
    public static async ValueTask<byte[]> ReadAsync(ModuleSession module, PidCommand command, CancellationToken ct = default)
    {
        var rsp = await module.RequestAsync(command.Request, ct).ConfigureAwait(false);
        if (rsp.Length >= 3 && rsp[0] == 0x7F) throw new NegativeResponseException(rsp[1], rsp[2]);
        if (rsp.Length < 3 || rsp[0] != 0x62 || rsp[1] != (byte)(command.Did >> 8) || rsp[2] != (byte)command.Did)
            throw new ProtocolException($"Unexpected answer to 22 {command.Did:X4}.");
        return rsp.AsSpan(3).ToArray();
    }
}
