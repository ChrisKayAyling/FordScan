using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FordDiag.Comms.Elm;

namespace FordDiag.Comms.Sniffer;

/// <summary>One frame seen on the bus.</summary>
/// <param name="Timestamp"><see cref="Stopwatch"/> timestamp (use <see cref="Stopwatch.GetElapsedTime(long)"/>).</param>
/// <param name="Id">CAN identifier when the adapter prints headers; null otherwise.</param>
public sealed record SniffedFrame(long Timestamp, uint? Id, bool Extended, byte[] Data)
{
    public string DataHex => Hex.ToString(Data, spaced: true);
    public string IdHex => Id is null ? "" : Extended ? Id.Value.ToString("X8") : Id.Value.ToString("X3");
}

/// <summary>Running per-identifier statistics (one row of a "bus monitor" table).</summary>
public sealed record SniffedMessage(uint? Id, bool Extended, byte[] LastData, long Count, TimeSpan LastPeriod)
{
    public string IdHex => Id is null ? "" : Extended ? Id.Value.ToString("X8") : Id.Value.ToString("X3");
}

public sealed class SnifferOptions
{
    /// <summary>Receive filter (ATCRA): 3 hex digits (11-bit) or 8 (29-bit). Null = all frames. The last 3 digits are used.</summary>
    public string? FilterId { get; set; }
    /// <summary>29-bit identifiers (ATSP 7/9). Default 11-bit.</summary>
    public bool Extended { get; set; }
    /// <summary>250 kbit/s instead of 500.</summary>
    public bool Speed250 { get; set; }
    /// <summary>Print identifiers (ATH1). Without them only data is reported.</summary>
    public bool ShowHeaders { get; set; } = true;
    /// <summary>Use STMA instead of ATMA. Null: automatic (STN adapters).</summary>
    public bool? UseStn { get; set; }
    /// <summary>Frames buffered between the adapter reader and the consumer; the oldest are dropped when full.</summary>
    public int QueueCapacity { get; set; } = 16384;
    /// <summary>Default batch interval for <see cref="CanSniffer.BatchesAsync"/> (~30 Hz).</summary>
    public TimeSpan UiInterval { get; set; } = TimeSpan.FromMilliseconds(33);
    public int MaxBatchSize { get; set; } = 2048;
}

/// <summary>
/// Passive CAN bus monitor on top of an ELM/STN adapter (ATMA / STMA). While running it owns the adapter.
/// The adapter is read at full speed by a background task; consumers get frames individually, in throttled batches
/// (for UI binding), or aggregated per identifier.
/// </summary>
public sealed class CanSniffer
{
    private readonly ElmTransport _elm;
    private long _dropped, _received;

    public CanSniffer(ElmTransport elm, SnifferOptions? options = null)
    {
        _elm = elm;
        Options = options ?? new SnifferOptions();
    }

    public SnifferOptions Options { get; }
    /// <summary>Frames discarded because the consumer was too slow.</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long ReceivedFrames => Interlocked.Read(ref _received);

    /// <summary>Starts monitoring and yields every frame until cancelled. The adapter is restored (monitor stopped) on exit.</summary>
    public async IAsyncEnumerable<SniffedFrame> FramesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var (reader, stop) = await StartAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var f in reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return f;
        }
        finally { await stop().ConfigureAwait(false); }
    }

    /// <summary>
    /// Yields lists of frames at most once per <paramref name="interval"/> (default <see cref="SnifferOptions.UiInterval"/>)
    /// so a UI can append them without flooding its dispatcher.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<SniffedFrame>> BatchesAsync(TimeSpan? interval = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var period = interval ?? Options.UiInterval;
        var (reader, stop) = await StartAsync(ct).ConfigureAwait(false);
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(period, ct).ConfigureAwait(false);
                var batch = new List<SniffedFrame>();
                while (batch.Count < Options.MaxBatchSize && reader.TryRead(out var f)) batch.Add(f);
                if (batch.Count > 0) yield return batch;
            }
        }
        finally { await stop().ConfigureAwait(false); }
    }

    /// <summary>Yields a snapshot of the per-identifier table (changed rows only) at most once per interval.</summary>
    public async IAsyncEnumerable<IReadOnlyList<SniffedMessage>> AggregatedAsync(TimeSpan? interval = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var table = new Dictionary<(uint?, bool), (byte[] Data, long Count, long LastTs, TimeSpan Period)>();
        await foreach (var batch in BatchesAsync(interval, ct).ConfigureAwait(false))
        {
            var changed = new Dictionary<(uint?, bool), SniffedMessage>();
            foreach (var f in batch)
            {
                var key = (f.Id, f.Extended);
                table.TryGetValue(key, out var row);
                var period = row.Count > 0 ? Stopwatch.GetElapsedTime(row.LastTs, f.Timestamp) : TimeSpan.Zero;
                row = (f.Data, row.Count + 1, f.Timestamp, period);
                table[key] = row;
                changed[key] = new SniffedMessage(f.Id, f.Extended, row.Data, row.Count, row.Period);
            }
            yield return changed.Values.OrderBy(m => m.Id).ToList();
        }
    }

    private async Task<(ChannelReader<SniffedFrame> Reader, Func<Task> Stop)> StartAsync(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<SniffedFrame>(new BoundedChannelOptions(Math.Max(16, Options.QueueCapacity))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        }, _ => Interlocked.Increment(ref _dropped));

        var conn = _elm.Connection;
        var lease = await conn.AcquireAsync(ct).ConfigureAwait(false);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task producer;
        try
        {
            await ConfigureAsync(conn, ct).ConfigureAwait(false);
            string cmd = (Options.UseStn ?? _elm.Info.IsStn) ? "STMA" : "ATMA";
            bool headers = Options.ShowHeaders;
            producer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var line in conn.MonitorLockedAsync(cmd, cts.Token).ConfigureAwait(false))
                    {
                        if (TryParseLine(line, headers, Stopwatch.GetTimestamp(), out var frame))
                        {
                            Interlocked.Increment(ref _received);
                            channel.Writer.TryWrite(frame);
                        }
                    }
                    channel.Writer.TryComplete();
                }
                catch (OperationCanceledException) { channel.Writer.TryComplete(); }
                catch (Exception e) { channel.Writer.TryComplete(e); }
            }, CancellationToken.None);
        }
        catch
        {
            cts.Dispose();
            lease.Dispose();
            throw;
        }

        async Task Stop()
        {
            cts.Cancel();
            try { await producer.ConfigureAwait(false); } catch { }
            cts.Dispose();
            // leave the adapter in a state the diagnostic layer expects
            try { await conn.SendLockedAsync("ATCRA", null, CancellationToken.None).ConfigureAwait(false); } catch { }
            _elm.InvalidateAddressing();
            lease.Dispose();
        }
        return (channel.Reader, Stop);
    }

    private async ValueTask ConfigureAsync(ElmConnection conn, CancellationToken ct)
    {
        await conn.SendLockedAsync("ATE0", null, ct).ConfigureAwait(false);
        await conn.SendLockedAsync("ATL0", null, ct).ConfigureAwait(false);
        await conn.SendLockedAsync(Options.ShowHeaders ? "ATH1" : "ATH0", null, ct).ConfigureAwait(false);
        await conn.SendLockedAsync("ATD0", null, ct).ConfigureAwait(false);
        await conn.SendLockedAsync("ATS1", null, ct).ConfigureAwait(false);   // spaces keep id/data separable
        string sp = Options.Extended ? (Options.Speed250 ? "9" : "7") : (Options.Speed250 ? "8" : "6");
        await conn.SendLockedAsync("ATSP " + sp, null, ct).ConfigureAwait(false);
        await conn.SendLockedAsync("ATAL", null, ct).ConfigureAwait(false);
        await conn.SendLockedAsync("ATCAF0", null, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(Options.FilterId))
        {
            var f = Options.FilterId.Trim();
            await conn.SendLockedAsync("ATCRA " + (f.Length > 3 && !Options.Extended ? f[^3..] : f), null, ct).ConfigureAwait(false);
        }
        else await conn.SendLockedAsync("ATCRA", null, ct).ConfigureAwait(false);
    }

    /// <summary>Parses one monitor line ("7E8 03 7F 22 11" with headers, or "03 7F 22 11" without).</summary>
    public static bool TryParseLine(string line, bool headers, long timestamp, out SniffedFrame frame)
    {
        frame = null!;
        var span = line.AsSpan().Trim();
        if (span.IsEmpty || span.StartsWith("<") || span.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            || span.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) || span.Contains("BUFFER", StringComparison.OrdinalIgnoreCase))
            return false;
        uint? id = null; bool ext = false;
        if (headers)
        {
            int sp = span.IndexOf(' ');
            var idText = sp < 0 ? span : span[..sp];
            if (idText.Length is not (3 or 8) || !uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            id = v; ext = idText.Length == 8;
            span = sp < 0 ? ReadOnlySpan<char>.Empty : span[(sp + 1)..];
        }
        if (!Hex.TryParse(span, out var data)) return false;
        frame = new SniffedFrame(timestamp, id, ext, data);
        return true;
    }
}
