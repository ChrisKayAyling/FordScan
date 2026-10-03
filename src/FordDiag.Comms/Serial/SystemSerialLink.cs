using System.IO.Ports;

namespace FordDiag.Comms.Serial;

/// <summary>System.IO.Ports implementation (USB serial, Bluetooth RFCOMM serial ports, FTDI/CH340/CP210x cables).</summary>
public sealed class SystemSerialLink : ISerialLink
{
    private readonly SerialPort _port;

    private SystemSerialLink(SerialPort port) => _port = port;

    public string Name => _port.PortName;
    public int BaudRate => _port.BaudRate;
    public bool IsOpen => _port.IsOpen;

    /// <summary>Opens the port off the calling thread (opening can block for a while on some drivers).</summary>
    public static async ValueTask<SystemSerialLink> OpenAsync(string portName, SerialLinkOptions options, CancellationToken ct = default)
    {
        var port = new SerialPort(portName, options.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = options.RtsCts ? Handshake.RequestToSend : Handshake.None,
            DtrEnable = options.AssertDtrRts || options.DsrDtr,
            ReadTimeout = SerialPort.InfiniteTimeout,
            WriteTimeout = 2000,
            ReadBufferSize = 16384,
        };
        try
        {
            await Task.Run(port.Open, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        {
            port.Dispose();
            throw new LinkClosedException($"Cannot open serial port {portName}: {e.Message}", e);
        }
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return new SystemSerialLink(port);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try { return await _port.BaseStream.ReadAsync(buffer, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return 0; }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { return 0; }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        try { await _port.BaseStream.WriteAsync(data, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException)
        {
            throw new LinkClosedException("Serial write failed: " + e.Message, e);
        }
    }

    public ValueTask SetBaudRateAsync(int baudRate, CancellationToken ct = default)
    {
        _port.BaudRate = baudRate;
        return ValueTask.CompletedTask;
    }

    public void DiscardInput()
    {
        try { if (_port.IsOpen) _port.DiscardInBuffer(); } catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        var p = _port;
        await Task.Run(() => { try { p.Dispose(); } catch { /* ignore */ } }).ConfigureAwait(false);
    }
}
