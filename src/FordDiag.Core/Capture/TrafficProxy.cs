using System.Net;
using System.Net.Sockets;
using System.Text;
using FordDiag.Comms.Serial;

namespace FordDiag.Core.Capture;

/// <summary>
/// Sits between a program (FORScan set to a WiFi/TCP adapter) and the real adapter, passing every byte through unchanged
/// and recording the conversation. Only one client is served at a time.
/// </summary>
public sealed class TrafficProxy : IAsyncDisposable
{
    private readonly Func<CancellationToken, ValueTask<ISerialLink>> _openAdapter;
    private readonly CaptureLog _log;
    private readonly IPAddress _bind;
    private readonly int _requestedPort;
    private TcpListener? _listener;
    private ISerialLink? _adapter;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _pendingBaud;

    public TrafficProxy(Func<CancellationToken, ValueTask<ISerialLink>> openAdapter, CaptureLog log, int port = 35000, bool allowRemote = false)
    {
        _openAdapter = openAdapter; _log = log; _requestedPort = port;
        _bind = allowRemote ? IPAddress.Any : IPAddress.Loopback;
    }

    public int Port { get; private set; }
    public bool HasClient { get; private set; }
    public long BytesToAdapter { get; private set; }
    public long BytesFromAdapter { get; private set; }
    public event Action? ClientChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _adapter = await _openAdapter(ct).ConfigureAwait(false);
        _listener = new TcpListener(_bind, _requestedPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _log.Add('i', $"Proxy listening on {_bind}:{Port}, adapter {_adapter.Name}");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            client.NoDelay = true;
            HasClient = true; ClientChanged?.Invoke();
            _log.Add('i', "Client connected: " + client.Client.RemoteEndPoint);
            try { await BridgeAsync(client, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { client.Dispose(); HasClient = false; ClientChanged?.Invoke(); _log.Add('i', "Client disconnected"); }
        }
    }

    private async Task BridgeAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = Task.Run(() => ClientToAdapterAsync(stream, linked.Token), linked.Token);
        var down = Task.Run(() => AdapterToClientAsync(stream, linked.Token), linked.Token);
        await Task.WhenAny(up, down).ConfigureAwait(false);
        linked.Cancel();
        try { await Task.WhenAll(up, down).ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private async Task ClientToAdapterAsync(NetworkStream stream, CancellationToken ct)
    {
        var split = new LineSplitter(false);
        var buf = new byte[1024];
        while (!ct.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return;
            await _adapter!.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);     // pass through first, log afterwards
            BytesToAdapter += n;
            foreach (var (kind, text) in split.Feed(buf.AsSpan(0, n)))
            {
                _log.Add('>', text);
                var baud = BaudChange(text);
                if (baud > 0) _pendingBaud = baud;
            }
        }
    }

    private async Task AdapterToClientAsync(NetworkStream stream, CancellationToken ct)
    {
        var split = new LineSplitter(true);
        var buf = new byte[1024];
        while (!ct.IsCancellationRequested)
        {
            int n = await _adapter!.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n == 0) return;
            await stream.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            BytesFromAdapter += n;
            foreach (var (kind, text) in split.Feed(buf.AsSpan(0, n)))
            {
                _log.Add(kind == 'P' ? 'P' : '<', text);
                if (kind == 'L' && _pendingBaud > 0 && text.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    // the adapter has accepted a UART speed change (ATBRD / STSBR): follow it so both ends keep talking
                    int baud = _pendingBaud; _pendingBaud = 0;
                    await _adapter.SetBaudRateAsync(baud, ct).ConfigureAwait(false);
                    _log.Add('i', $"Adapter UART speed changed to {baud}");
                }
            }
        }
    }

    /// <summary>"ATBRD 23" -> 4,000,000/0x23 baud; "STSBR 115200" / "STBR 115200" -> that speed; otherwise 0.</summary>
    public static int BaudChange(string command)
    {
        var c = new string(command.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        if (c.StartsWith("ATBRD") && int.TryParse(c[5..], System.Globalization.NumberStyles.HexNumber, null, out var div) && div > 0) return (int)Math.Round(4_000_000.0 / div);
        foreach (var p in new[] { "STSBR", "STBR" })
            if (c.StartsWith(p) && int.TryParse(c[p.Length..], out var baud) && baud > 0) return baud;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        if (_acceptLoop is not null) { try { await _acceptLoop.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException) { } }
        if (_adapter is not null) await _adapter.DisposeAsync().ConfigureAwait(false);
        _cts?.Dispose();
    }
}
