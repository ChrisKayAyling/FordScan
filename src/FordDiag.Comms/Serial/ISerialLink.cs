namespace FordDiag.Comms.Serial;

/// <summary>
/// Minimal async byte link to an OBD adapter: a real serial port, a TCP socket (WiFi ELM), or an in-memory
/// pipe connected to a simulated device. All members are thread-safe for one reader plus one writer.
/// </summary>
public interface ISerialLink : IAsyncDisposable
{
    /// <summary>Port name or endpoint (e.g. "COM3", "/dev/ttyUSB0", "192.168.0.10:35000").</summary>
    string Name { get; }

    /// <summary>Current UART speed; 0 for non-UART links (TCP).</summary>
    int BaudRate { get; }

    bool IsOpen { get; }

    /// <summary>Reads at least one byte; returns 0 when the link is closed by the peer.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>Changes the UART speed at runtime (no-op for TCP).</summary>
    ValueTask SetBaudRateAsync(int baudRate, CancellationToken ct = default);

    /// <summary>Drops any bytes buffered but not yet read.</summary>
    void DiscardInput();
}

/// <summary>Serial framing / flow-control options used when opening a real port.</summary>
public sealed record SerialLinkOptions
{
    public int BaudRate { get; init; } = 38400;
    /// <summary>Hardware flow control (OBDLink SX/EX).</summary>
    public bool RtsCts { get; init; }
    public bool DsrDtr { get; init; }
    /// <summary>Drive DTR/RTS high after open (needed by some USB-serial bridges to release the adapter).</summary>
    public bool AssertDtrRts { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
