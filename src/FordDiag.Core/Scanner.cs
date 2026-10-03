using System.Runtime.CompilerServices;
using FordDiag.Comms;

namespace FordDiag.Core;

public sealed record FoundModule(FordModule Module, FordBus Bus, byte[] ProbeResponse)
{
    public bool Negative => ProbeResponse.Length > 0 && ProbeResponse[0] == 0x7F;
}

public sealed class ScanOptions
{
    public IReadOnlyList<FordBus> Buses { get; init; } = new[] { FordBus.HsCan, FordBus.MsCan };
    /// <summary>Also probe every other request id 0x700-0x7DF (response = request + 8), like FORScan's full scan.</summary>
    public bool FullRange { get; init; }
    public BusSwitching Switching { get; init; } = BusSwitching.Automatic;
    /// <summary>Called before probing a bus when the adapter needs a manual HS/MS switch flip. Throw/cancel to abort.</summary>
    public Func<FordBus, CancellationToken, Task>? PromptBusSwitch { get; init; }
    public IProgress<string>? Log { get; init; }
}

public static class FordScanner
{
    /// <summary>
    /// Probes modules with ReadDataByIdentifier F190 (VIN). Any answer, including a negative response, means the module is present.
    /// Buses are scanned one after the other so a manual switch is flipped at most once.
    /// </summary>
    public static async IAsyncEnumerable<FoundModule> ScanAsync(IAddressableTransport t, ScanOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        options ??= new ScanOptions();
        var seen = new HashSet<uint>();
        var candidates = FordModules.All.ToList();
        if (options.FullRange)
            for (uint id = 0x700; id <= 0x7DF; id++)
                if (candidates.All(c => c.RequestId != id)) candidates.Add(FordModules.Unknown(id));

        foreach (var bus in options.Buses)
        {
            if (bus == FordBus.MsCan && options.Switching == BusSwitching.None)
            { options.Log?.Report("Adapter has no MS-CAN access: skipping MS-CAN."); continue; }
            if (options.Switching == BusSwitching.Manual && options.PromptBusSwitch is { } prompt)
                await prompt(bus, ct).ConfigureAwait(false);

            options.Log?.Report($"Scanning {bus.Label()}...");
            bool first = true;
            foreach (var m in candidates)
            {
                if (seen.Contains(m.RequestId)) continue;
                ct.ThrowIfCancellationRequested();
                byte[]? rsp = null;
                try
                {
                    await t.ConnectEcuAsync(m.ToAddress(bus), ct).ConfigureAwait(false);
                    rsp = await t.RequestAsync(new byte[] { 0x22, 0xF1, 0x90 }, ct).ConfigureAwait(false);
                }
                catch (EcuTimeoutException) { }
                catch (ElmErrorException ex) when (first)
                {
                    options.Log?.Report($"{bus.Label()} not reachable ({ex.ElmMessage}); skipping bus.");
                    break;
                }
                catch (ElmErrorException) { }
                first = false;
                if (rsp is { Length: > 0 })
                {
                    seen.Add(m.RequestId);
                    yield return new FoundModule(m, bus, rsp);
                }
            }
        }
    }
}
