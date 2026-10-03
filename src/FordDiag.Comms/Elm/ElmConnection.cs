using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using FordDiag.Comms.Serial;

namespace FordDiag.Comms.Elm;

/// <summary>
/// Low-level ELM327/STN command channel: one command in flight, prompt based framing over an <see cref="ISerialLink"/>.
/// All public methods are safe to call from multiple tasks (they serialise on an async lock).
/// </summary>
public sealed class ElmConnection : IAsyncDisposable
{
    private readonly ElmChannel _channel;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _needsResync;
    private long _lastActivityTicks;

    public ElmConnection(ISerialLink link, TimeSpan? defaultTimeout = null, Action<string>? trace = null)
    {
        Link = link;
        _channel = new ElmChannel(link);
        DefaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(5);
        Trace = trace;
    }

    public ISerialLink Link { get; }
    public TimeSpan DefaultTimeout { get; set; }
    public Action<string>? Trace { get; set; }
    public ElmCounters Counters { get; } = new();

    /// <summary>Time since the last command completed.</summary>
    public TimeSpan IdleTime => _clock.Elapsed - TimeSpan.FromTicks(_lastActivityTicks);

    /// <summary>Exclusive access lease; dispose to release.</summary>
    public readonly struct Lease : IDisposable
    {
        private readonly SemaphoreSlim? _g;
        internal Lease(SemaphoreSlim g) => _g = g;
        public void Dispose() => _g?.Release();
    }

    /// <summary>Takes the exclusive lock so a multi-command exchange cannot be interleaved.</summary>
    public async ValueTask<Lease> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(_gate);
    }

    /// <summary>Non-blocking acquire (used by the background keep-alive). Returns false when the adapter is busy.</summary>
    public bool TryAcquire(out Lease lease)
    {
        if (_gate.Wait(0)) { lease = new Lease(_gate); return true; }
        lease = default; return false;
    }

    /// <summary>Sends a command and waits for the prompt (acquires the lock).</summary>
    public async ValueTask<ElmReply> SendAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var _ = await AcquireAsync(ct).ConfigureAwait(false);
        return await SendLockedAsync(command, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Same as <see cref="SendAsync"/> but the caller already holds the lease.</summary>
    public async ValueTask<ElmReply> SendLockedAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_needsResync) await ResyncLockedAsync(ct).ConfigureAwait(false);
        command = command.Trim().ToUpperInvariant();
        Trace?.Invoke("> " + command);
        _channel.Discard();
        try
        {
            await _channel.WriteAsync(Encoding.ASCII.GetBytes(command + "\r"), ct).ConfigureAwait(false);
            var read = await _channel.ReadUntilPromptAsync(timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
            if (read.Closed) throw new LinkClosedException("The adapter closed the connection.");
            if (read.TimedOut) _needsResync = true;
            var reply = ElmReply.Parse(read.Text, command, read.TimedOut);
            Count(reply);
            Trace?.Invoke("< " + (read.TimedOut ? "[TIMEOUT] " : "") + reply.Text.Replace('\n', '|'));
            return reply;
        }
        catch (OperationCanceledException)
        {
            _needsResync = true;
            throw;
        }
        finally
        {
            _lastActivityTicks = _clock.Elapsed.Ticks;
        }
    }

    /// <summary>Writes raw bytes without waiting (e.g. the single stop character for monitor mode).</summary>
    internal ValueTask WriteRawAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _channel.WriteAsync(data, ct);

    /// <summary>
    /// Re-establishes prompt synchronisation after a timeout/cancellation: sends "ATE0" (harmless, always answers OK) until an "OK" arrives,
    /// discarding any stale output before it.
    /// </summary>
    private async ValueTask ResyncLockedAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await _channel.WriteAsync("ATE0\r"u8.ToArray(), ct).ConfigureAwait(false);
            for (int i = 0; i < 4; i++)
            {
                var r = await _channel.ReadUntilPromptAsync(TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
                if (r.Closed) throw new LinkClosedException("The adapter closed the connection.");
                if (r.Text.Contains("OK", StringComparison.Ordinal)) { _needsResync = false; _channel.Discard(); return; }
                if (r.TimedOut) break;
            }
        }
        throw new EcuTimeoutException("Adapter does not respond (resynchronisation failed).");
    }

    /// <summary>
    /// Runs a streaming command such as "ATMA"/"STMA" and yields each output line until the caller stops enumerating
    /// (or <paramref name="ct"/> fires). On exit a stop character is sent and the prompt consumed. The caller must hold the lease.
    /// </summary>
    public async IAsyncEnumerable<string> MonitorLockedAsync(string command, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_needsResync) await ResyncLockedAsync(ct).ConfigureAwait(false);
        command = command.Trim().ToUpperInvariant();
        Trace?.Invoke("> " + command + " (monitor)");
        _channel.Discard();
        await _channel.WriteAsync(Encoding.ASCII.GetBytes(command + "\r"), ct).ConfigureAwait(false);
        string cmdKey = command.Replace(" ", "");
        bool promptSeen = false;
        try
        {
            while (true)
            {
                var (line, prompt, closed) = await _channel.ReadLineOrPromptAsync(ct).ConfigureAwait(false);
                if (closed) throw new LinkClosedException("The adapter closed the connection.");
                if (line is not null)
                {
                    var l = line.Trim().ToUpperInvariant();
                    if (l.Length == 0 || l.Replace(" ", "") == cmdKey) continue;
                    yield return l;
                }
                if (prompt) { promptSeen = true; yield break; }
            }
        }
        finally
        {
            if (!promptSeen)
            {
                // Any character stops the monitor; then wait for the prompt (best effort, bounded).
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _channel.WriteAsync(" "u8.ToArray(), cts.Token).ConfigureAwait(false);
                    var r = await _channel.ReadUntilPromptAsync(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
                    if (r.TimedOut) _needsResync = true;
                }
                catch { _needsResync = true; }
            }
            _lastActivityTicks = _clock.Elapsed.Ticks;
        }
    }

    private void Count(ElmReply r)
    {
        if (r.TimedOut) Counters.Timeout++;
        switch (r.Error)
        {
            case "?": Counters.Question++; break;
            case "NO DATA": Counters.NoData++; break;
            case "CAN ERROR": Counters.Can++; break;
            case "BUFFER FULL": Counters.BufferFull++; break;
            case "RX ERROR": Counters.Rx++; break;
        }
    }

    /// <summary>Writes raw text (no CR appended) and waits for <paramref name="token"/>; used for baud-rate switch handshakes.</summary>
    internal async ValueTask<(bool Found, string Text)> WriteAndWaitTokenAsync(string text, string token, TimeSpan timeout, CancellationToken ct)
    {
        _channel.Discard();
        await _channel.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
        return await _channel.ReadUntilTokenAsync(token, timeout, ct).ConfigureAwait(false);
    }

    internal ValueTask<(bool Found, string Text)> WaitTokenAsync(string token, TimeSpan timeout, CancellationToken ct) =>
        _channel.ReadUntilTokenAsync(token, timeout, ct);

    /// <summary>Changes the UART speed on the host side only (the adapter must already have been told).</summary>
    public ValueTask SetHostBaudAsync(int baud, CancellationToken ct = default) => Link.SetBaudRateAsync(baud, ct);

    /// <summary>Marks the connection for resynchronisation before the next command.</summary>
    internal void MarkDirty() => _needsResync = true;

    /// <summary>Drops stale input.</summary>
    internal void Discard() => _channel.Discard();

    internal ValueTask<ElmChannel.PromptRead> ReadPromptAsync(TimeSpan timeout, CancellationToken ct) =>
        _channel.ReadUntilPromptAsync(timeout, ct);

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
