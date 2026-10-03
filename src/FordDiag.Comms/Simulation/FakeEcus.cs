namespace FordDiag.Comms.Simulation;

/// <summary>
/// Scripted ISO 15765-2 CAN ECU: reassembles requests (sending flow control), runs an <see cref="EcuScript"/>,
/// segments answers and honours the tester's flow control.
/// </summary>
public sealed class FakeCanEcu : ISimulatedEcu
{
    private readonly object _lock = new();
    private readonly IsoTpReassembler _rx;
    private readonly Queue<OutMessage> _out = new();
    private OutMessage? _current;
    private int _blockLeft = -1;       // frames still allowed in the current block (-1: unlimited)
    private readonly int _ext;
    private int _cfInBlock;

    private sealed class OutMessage
    {
        public Queue<byte[]> Frames = new();
        public TimeSpan Delay;
        public bool FirstSent;
    }

    public FakeCanEcu(string name, uint requestId, uint responseId, EcuScript script, bool is29Bit = false, byte? extAddress = null)
    {
        Name = name; RequestId = requestId; ResponseId = responseId; Script = script; Is29Bit = is29Bit; ExtAddress = extAddress;
        _ext = extAddress.HasValue ? 1 : 0;
        _rx = new IsoTpReassembler(_ext);
    }

    public string Name { get; }
    /// <summary>CAN id the tester transmits to.</summary>
    public uint RequestId { get; }
    /// <summary>CAN id the ECU answers on.</summary>
    public uint ResponseId { get; }
    public bool Is29Bit { get; }
    public byte? ExtAddress { get; }
    public EcuScript Script { get; }

    /// <summary>Block size the ECU announces in its flow control to the tester (0 = no limit).</summary>
    public int BlockSize { get; set; }
    /// <summary>STmin byte announced to the tester.</summary>
    public byte StMin { get; set; }
    /// <summary>When set, the ECU never reacts to requests (dead ECU).</summary>
    public bool Offline { get; set; }
    /// <summary>Pad all frames to 8 bytes with this value.</summary>
    public byte? PadByte { get; set; }

    public void OnFrame(BusFrame tx, Action<BusFrame> emit)
    {
        if (Offline || tx.Id != RequestId || tx.Extended != Is29Bit || tx.Data.Length == 0) return;
        if (ExtAddress is byte ea && tx.Data[0] != ea) return;
        lock (_lock)
        {
            IsoTpFrameType type;
            try { type = IsoTp.TypeOf(tx.Data, _ext); } catch (ProtocolException) { return; }
            switch (type)
            {
                case IsoTpFrameType.FlowControl:
                {
                    if (_current is null) return;
                    var fc = IsoTp.ParseFlowControl(tx.Data, _ext);
                    if (!fc.ClearToSend) return;
                    _blockLeft = fc.BlockSize == 0 ? -1 : fc.BlockSize;
                    Pump(emit);
                    break;
                }
                case IsoTpFrameType.Single:
                case IsoTpFrameType.First:
                {
                    _out.Clear(); _current = null;   // new request abandons any unfinished transmission
                    _rx.Reset();
                    try { _rx.Feed(tx.Data); } catch (ProtocolException) { return; }
                    _cfInBlock = 0;
                    if (type == IsoTpFrameType.First)
                        Emit(emit, IsoTp.BuildFlowControl(0, BlockSize, StMin, ExtAddress), TimeSpan.Zero);
                    else Complete(emit);
                    break;
                }
                case IsoTpFrameType.Consecutive:
                {
                    if (!_rx.InProgress) return;
                    try { _rx.Feed(tx.Data); } catch (ProtocolException) { _rx.Reset(); return; }
                    if (_rx.IsComplete) Complete(emit);
                    else if (BlockSize > 0 && ++_cfInBlock == BlockSize)
                    {
                        _cfInBlock = 0;   // block done: grant the next one
                        Emit(emit, IsoTp.BuildFlowControl(0, BlockSize, StMin, ExtAddress), TimeSpan.Zero);
                    }
                    break;
                }
            }
        }
    }

    private void Complete(Action<BusFrame> emit)
    {
        var request = _rx.Payload;
        _rx.Reset();
        foreach (var reply in Script.Handle(request))
        {
            var frames = IsoTp.Segment(reply.Payload, ExtAddress, PadByte);
            _out.Enqueue(new OutMessage { Frames = new Queue<byte[]>(frames), Delay = reply.Delay });
        }
        _current = null;
        _blockLeft = -1;
        Pump(emit);
    }

    /// <summary>Sends frames of the current message until flow control requires waiting; then moves to the next message.</summary>
    private void Pump(Action<BusFrame> emit)
    {
        while (true)
        {
            if (_current is null)
            {
                if (!_out.TryDequeue(out _current)) return;
                _blockLeft = -1;
            }
            var m = _current;
            while (m.Frames.Count > 0)
            {
                bool isFirst = !m.FirstSent;
                if (!isFirst && _blockLeft == 0) return;   // wait for the tester's next flow control
                var f = m.Frames.Dequeue();
                Emit(emit, f, isFirst ? m.Delay : TimeSpan.Zero);
                if (isFirst)
                {
                    m.FirstSent = true;
                    if (m.Frames.Count > 0) { _blockLeft = 0; return; }   // multi-frame: wait for FC after the first frame
                }
                else if (_blockLeft > 0) _blockLeft--;
            }
            _current = null;
        }
    }

    private void Emit(Action<BusFrame> emit, byte[] data, TimeSpan delay) =>
        emit(new BusFrame(ResponseId, Is29Bit, data, delay));
}

/// <summary>Scripted K-line ECU (KWP2000 / ISO 9141).</summary>
public sealed class FakeKwpEcu : ISimulatedKLineEcu
{
    public FakeKwpEcu(string name, byte address, EcuScript script, bool fastInit = true, bool slowInit = true)
    {
        Name = name; Address = address; Script = script; SupportsFastInit = fastInit; SupportsSlowInit = slowInit;
    }

    public string Name { get; }
    public byte Address { get; }
    public EcuScript Script { get; }
    public bool SupportsFastInit { get; }
    public bool SupportsSlowInit { get; }

    public IReadOnlyList<EcuReply> Handle(byte[] request)
    {
        if (request.Length == 1 && request[0] == 0x81)
        {
            var r = Script.Handle(request);
            // startCommunication default answer if the script has no explicit rule (script default is NRC 0x11)
            if (r.Count == 1 && r[0].Payload.Length >= 3 && r[0].Payload[0] == 0x7F)
                return new[] { new EcuReply(new byte[] { 0xC1, 0xEF, 0x8F }) };
            return r;
        }
        return Script.Handle(request);
    }
}
