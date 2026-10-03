using System.Buffers;
using System.IO.Pipelines;

namespace FordDiag.Comms.Serial;

/// <summary>
/// In-memory duplex link. <see cref="CreatePair"/> returns the host end (give it to the driver) and the device end
/// (give it to <c>SimulatedElm</c>). Bytes written on one end are read on the other. Never blocks writers.
/// </summary>
public sealed class InMemorySerialLink : ISerialLink
{
    private readonly PipeReader _reader;
    private readonly PipeWriter _writer;
    private int _baud;
    private volatile bool _closed;

    private InMemorySerialLink(string name, PipeReader reader, PipeWriter writer, int baud)
    {
        Name = name; _reader = reader; _writer = writer; _baud = baud;
    }

    /// <summary>The other end of the pair.</summary>
    public InMemorySerialLink Peer { get; private set; } = null!;

    public string Name { get; }
    public int BaudRate => _baud;
    public bool IsOpen => !_closed;

    /// <summary>Creates a connected pair (host, device). The initial baud rate of both ends is <paramref name="baud"/>.</summary>
    public static (InMemorySerialLink Host, InMemorySerialLink Device) CreatePair(int baud = 38400)
    {
        var opts = new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false);
        var a2b = new Pipe(opts);
        var b2a = new Pipe(opts);
        var host = new InMemorySerialLink("mem:host", b2a.Reader, a2b.Writer, baud);
        var dev = new InMemorySerialLink("mem:device", a2b.Reader, b2a.Writer, baud);
        host.Peer = dev; dev.Peer = host;
        return (host, dev);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;
        var result = await _reader.ReadAsync(ct).ConfigureAwait(false);
        var seq = result.Buffer;
        if (seq.IsEmpty)
        {
            _reader.AdvanceTo(seq.End);
            return 0; // completed
        }
        int n = (int)Math.Min(seq.Length, buffer.Length);
        var slice = seq.Slice(0, n);
        slice.CopyTo(buffer.Span);
        _reader.AdvanceTo(slice.End);
        return n;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_closed) throw new LinkClosedException("In-memory link closed.");
        await _writer.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public ValueTask SetBaudRateAsync(int baudRate, CancellationToken ct = default)
    {
        _baud = baudRate;
        return ValueTask.CompletedTask;
    }

    public void DiscardInput()
    {
        try
        {
            while (_reader.TryRead(out var r))
            {
                _reader.AdvanceTo(r.Buffer.End);
                if (r.IsCompleted || r.Buffer.IsEmpty) break;
            }
        }
        catch (InvalidOperationException) { /* a read is pending: nothing buffered */ }
    }

    public ValueTask DisposeAsync()
    {
        if (_closed) return ValueTask.CompletedTask;
        _closed = true;
        _writer.Complete();   // peer sees EOF
        _reader.Complete();
        return ValueTask.CompletedTask;
    }
}
