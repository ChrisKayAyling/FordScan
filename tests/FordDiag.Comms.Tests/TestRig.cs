using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Comms.Simulation;

namespace FordDiag.Comms.Tests;

/// <summary>Wires a simulated bus + ELM to an <see cref="ElmTransport"/>.</summary>
public sealed class TestRig : IAsyncDisposable
{
    public SimulatedBus Bus { get; }
    public SimulatedElm Device { get; }
    public ElmTransport Elm { get; private set; } = null!;

    private TestRig(SimulatedBus bus, SimulatedElm dev) { Bus = bus; Device = dev; }


    public static async Task<TestRig> CreateAsync(Action<SimulatedBus>? setup = null, SimulatedElmOptions? elmOptions = null,
        Action<ElmOptions>? configure = null, int hostBaud = 38400)
    {
        var bus = new SimulatedBus();
        setup?.Invoke(bus);
        var (host, dev) = SimulatedElm.Create(bus, elmOptions, hostBaud);
        var rig = new TestRig(bus, dev);
        var opt = new ElmOptions
        {
            FirstProbeTimeout = TimeSpan.FromMilliseconds(500),
            FallbackProbeTimeout = TimeSpan.FromMilliseconds(150),
            RequestTimeout = TimeSpan.FromSeconds(2),
            CommandTimeout = TimeSpan.FromSeconds(2),
            PendingTimeout = TimeSpan.FromSeconds(2),
        };
        configure?.Invoke(opt);
        rig.Elm = await ElmTransport.ConnectAsync(host, opt);
        return rig;
    }

    public async ValueTask DisposeAsync()
    {
        await Elm.DisposeAsync();
        await Device.DisposeAsync();
    }
}
