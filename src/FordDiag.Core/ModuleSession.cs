using FordDiag.Comms;
using FordDiag.Comms.Diagnostics;

namespace FordDiag.Core;

/// <summary>Diagnostic conversation with one module on one bus.</summary>
public sealed class ModuleSession
{
    private readonly IAddressableTransport _t;

    public ModuleSession(IAddressableTransport transport, FordModule module, FordBus bus)
    { _t = transport; Module = module; Bus = bus; }

    public FordModule Module { get; }
    public FordBus Bus { get; }

    public ValueTask OpenAsync(CancellationToken ct = default) => _t.ConnectEcuAsync(Module.ToAddress(Bus), ct);

    public async ValueTask<byte[]> ReadDidAsync(ushort did, CancellationToken ct = default)
    {
        await OpenAsync(ct).ConfigureAwait(false);
        return await _t.ReadDataByIdentifierAsync(did, ct).ConfigureAwait(false);
    }

    /// <summary>Reads the identification DIDs; modules that don't support one are skipped.</summary>
    public async ValueTask<IReadOnlyList<(ushort Did, string Name, string Value)>> ReadIdentificationAsync(CancellationToken ct = default)
    {
        await OpenAsync(ct).ConfigureAwait(false);
        var list = new List<(ushort, string, string)>();
        foreach (var (did, name, ascii) in FordDids.Identification)
        {
            try { list.Add((did, name, FordDids.Format(await _t.ReadDataByIdentifierAsync(did, ct).ConfigureAwait(false), ascii))); }
            catch (NegativeResponseException) { /* DID not supported by this module */ }
        }
        return list;
    }

    public async ValueTask<IReadOnlyList<Dtc>> ReadDtcsAsync(byte statusMask = 0xAF, CancellationToken ct = default)
    {
        await OpenAsync(ct).ConfigureAwait(false);
        return await _t.ReadDtcsUdsAsync(statusMask, ct).ConfigureAwait(false);
    }

    public async ValueTask ClearDtcsAsync(CancellationToken ct = default)
    {
        await OpenAsync(ct).ConfigureAwait(false);
        await _t.ClearDtcsUdsAsync(0xFFFFFF, ct).ConfigureAwait(false);
    }

    public async ValueTask<byte[]> RequestAsync(byte[] request, CancellationToken ct = default)
    {
        await OpenAsync(ct).ConfigureAwait(false);
        return await _t.RequestAsync(request, ct).ConfigureAwait(false);
    }
}
