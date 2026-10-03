using FordDiag.Comms;

namespace FordDiag.Comms.Simulation;

/// <summary>
/// Hardware-free transport: routes requests straight to <see cref="EcuScript"/>s (no ELM, no framing).
/// ECUs are selected by CAN TX id, K-line functional address, or name. "Response pending" replies are skipped
/// and the final reply returned, like the real drivers do.
/// </summary>
public sealed class SimulatedTransport : IAddressableTransport
{
    private sealed record Entry(string Name, uint? TxId, byte? FuncAddress, EcuScript Script);
    private readonly List<Entry> _ecus = new();
    private EcuScript? _current;

    public EcuAddress? CurrentEcu { get; private set; }

    /// <summary>Real delay per request (default none). Reply delays are honoured scaled by <see cref="DelayScale"/>.</summary>
    public TimeSpan Latency { get; set; }
    /// <summary>0 = ignore EcuReply.Delay (default, fast), 1 = real time.</summary>
    public double DelayScale { get; set; }

    public SimulatedTransport AddCan(string name, uint txId, EcuScript script) { _ecus.Add(new Entry(name, txId, null, script)); return this; }
    public SimulatedTransport AddKLine(string name, byte funcAddress, EcuScript script) { _ecus.Add(new Entry(name, null, funcAddress, script)); return this; }

    public ValueTask ConnectEcuAsync(EcuAddress ecu, CancellationToken ct = default)
    {
        var e = _ecus.FirstOrDefault(x => (x.TxId.HasValue && x.TxId == ecu.TxId && ecu.TxId != 0)
                                          || (x.FuncAddress.HasValue && x.FuncAddress == ecu.FuncAddress)
                                          || (ecu.Name.Length > 0 && x.Name == ecu.Name));
        if (e is null) throw new EcuTimeoutException($"Simulated bus has no ECU '{ecu.Name}' (tx {ecu.TxId:X}).");
        _current = e.Script; CurrentEcu = ecu;
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseProtocolAsync(CancellationToken ct = default) { _current = null; CurrentEcu = null; return ValueTask.CompletedTask; }

    public async ValueTask<byte[]> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
    {
        var script = _current ?? throw new InvalidOperationException("No ECU selected.");
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct).ConfigureAwait(false);
        var replies = script.Handle(request.ToArray());
        foreach (var r in replies)
        {
            if (IsoTp.IsPending(r.Payload, request.Span[0])) continue;
            if (DelayScale > 0 && r.Delay > TimeSpan.Zero) await Task.Delay(r.Delay * DelayScale, ct).ConfigureAwait(false);
            return r.Payload;
        }
        throw new EcuTimeoutException("No response from simulated ECU.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
