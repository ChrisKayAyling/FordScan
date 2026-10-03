namespace FordDiag.Comms.Simulation;

/// <summary>A CAN frame (or, for K-line, a message with Id 0) on the simulated bus.</summary>
public readonly record struct BusFrame(uint Id, bool Extended, byte[] Data, TimeSpan Delay = default);

/// <summary>An ECU attached to a <see cref="SimulatedBus"/>.</summary>
public interface ISimulatedEcu
{
    string Name { get; }
    /// <summary>Called for every frame the tester transmits; the ECU emits answers through <paramref name="emit"/>.</summary>
    void OnFrame(BusFrame tx, Action<BusFrame> emit);
}

/// <summary>A K-line (KWP2000 / ISO 9141) ECU attached to a <see cref="SimulatedBus"/>.</summary>
public interface ISimulatedKLineEcu
{
    string Name { get; }
    byte Address { get; }
    bool SupportsFastInit { get; }
    bool SupportsSlowInit { get; }
    IReadOnlyList<EcuReply> Handle(byte[] request);
}

/// <summary>
/// Virtual vehicle bus shared by <see cref="SimulatedElm"/> instances: ECUs, a receive queue for the adapter and
/// optional background traffic (for the sniffer).
/// </summary>
public sealed class SimulatedBus
{
    private readonly List<ISimulatedEcu> _ecus = new();
    private readonly List<ISimulatedKLineEcu> _kecus = new();
    private readonly Queue<BusFrame> _rx = new();
    private readonly object _lock = new();
    private TaskCompletionSource _signal = NewSignal();
    private int _monitors;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>CAN speed of the virtual bus in kbit/s. An adapter configured for another speed gets "CAN ERROR".</summary>
    public int SpeedKbps { get; set; } = 500;

    public IReadOnlyList<ISimulatedEcu> Ecus { get { lock (_lock) return _ecus.ToArray(); } }

    public SimulatedBus Add(ISimulatedEcu ecu) { lock (_lock) _ecus.Add(ecu); return this; }
    public SimulatedBus Add(ISimulatedKLineEcu ecu) { lock (_lock) _kecus.Add(ecu); return this; }

    public ISimulatedKLineEcu? FindKLine(byte address) { lock (_lock) return _kecus.FirstOrDefault(e => e.Address == address); }
    public bool HasCanEcus { get { lock (_lock) return _ecus.Count > 0; } }

    /// <summary>Tester transmits a frame; ECU answers are queued (see <see cref="TryTake"/>).</summary>
    public void Transmit(BusFrame tx)
    {
        ISimulatedEcu[] ecus;
        lock (_lock) ecus = _ecus.ToArray();
        foreach (var e in ecus) e.OnFrame(tx, Enqueue);
    }

    public void Enqueue(BusFrame f)
    {
        lock (_lock) _rx.Enqueue(f);
        Wake();
    }

    /// <summary>Discards frames the adapter would have missed (everything not yet read).</summary>
    public void ClearRx() { lock (_lock) _rx.Clear(); }

    /// <summary>Takes the next queued frame whose delay does not exceed <paramref name="within"/>.</summary>
    public bool TryTake(TimeSpan within, Func<BusFrame, bool>? filter, out BusFrame frame)
    {
        lock (_lock)
        {
            // Frames are kept in arrival order; filtered-out frames are dropped (adapter filter), too-late frames stop the scan.
            while (_rx.Count > 0)
            {
                var f = _rx.Peek();
                if (f.Delay > within) break;
                _rx.Dequeue();
                if (filter is null || filter(f)) { frame = f; return true; }
            }
        }
        frame = default;
        return false;
    }

    /// <summary>Takes any queued frame regardless of its delay (monitor mode).</summary>
    public bool TryTakeAny(Func<BusFrame, bool>? filter, out BusFrame frame) => TryTake(TimeSpan.MaxValue, filter, out frame);

    /// <summary>Completes when a frame is available.</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_rx.Count > 0) return Task.CompletedTask;
            return _signal.Task.WaitAsync(ct);
        }
    }

    private void Wake()
    {
        TaskCompletionSource old;
        lock (_lock) { old = _signal; _signal = NewSignal(); }
        old.TrySetResult();
    }

    internal void MonitorStarted() => Interlocked.Increment(ref _monitors);
    internal void MonitorStopped() => Interlocked.Decrement(ref _monitors);
    public bool IsMonitored => Volatile.Read(ref _monitors) > 0;

    /// <summary>Background traffic: delivered only while an adapter is in monitor mode (like a real bus).</summary>
    public void Publish(BusFrame frame)
    {
        if (IsMonitored) Enqueue(frame);
    }

    /// <summary>Generates periodic background frames until cancelled (for sniffer demos/tests).</summary>
    public async Task GenerateTrafficAsync(IReadOnlyList<(uint Id, byte[] Data)> frames, TimeSpan period, CancellationToken ct)
    {
        int i = 0;
        using var timer = new PeriodicTimer(period);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var (id, data) = frames[i++ % frames.Count];
                Publish(new BusFrame(id, id > 0x7FF, data));
            }
        }
        catch (OperationCanceledException) { }
    }
}
