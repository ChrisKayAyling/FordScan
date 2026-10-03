using System.Net.Sockets;

namespace FordDiag.Comms.Serial;

/// <summary>TCP link to a WiFi ELM327 (default 192.168.0.10:35000) or any serial-over-TCP bridge.</summary>
public sealed class TcpSerialLink : ISerialLink
{
    private readonly TcpClient _client = new() { NoDelay = true };
    private NetworkStream? _stream;

    public TcpSerialLink(string host, int port)
    {
        Host = host; Port = port;
    }

    public string Host { get; }
    public int Port { get; }
    public string Name => $"{Host}:{Port}";
    public int BaudRate => 0;
    public bool IsOpen => _stream is not null && _client.Connected;

    /// <summary>Connects (with timeout) and returns an open link.</summary>
    public static async ValueTask<TcpSerialLink> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var link = new TcpSerialLink(host, port);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await link._client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            link._client.Dispose();
            throw new EcuTimeoutException($"Timed out connecting to {host}:{port}");
        }
        catch (SocketException e)
        {
            link._client.Dispose();
            throw new LinkClosedException($"Cannot connect to {host}:{port}: {e.Message}", e);
        }
        link._stream = link._client.GetStream();
        return link;
    }

    private NetworkStream Stream => _stream ?? throw new LinkClosedException("TCP link not connected.");

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try { return await Stream.ReadAsync(buffer, ct).ConfigureAwait(false); }
        catch (IOException) { return 0; }
        catch (ObjectDisposedException) { return 0; }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        try { await Stream.WriteAsync(data, ct).ConfigureAwait(false); }
        catch (IOException e) { throw new LinkClosedException("TCP write failed.", e); }
    }

    public ValueTask SetBaudRateAsync(int baudRate, CancellationToken ct = default) => ValueTask.CompletedTask;

    public void DiscardInput()
    {
        var s = _stream;
        if (s is null) return;
        var tmp = new byte[256];
        while (s.DataAvailable) { if (s.Read(tmp, 0, tmp.Length) <= 0) break; }
    }

    public ValueTask DisposeAsync()
    {
        _stream = null;
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
