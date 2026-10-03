using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using FordDiag.Comms.Serial;

namespace FordDiag.Comms.Elm;

/// <summary>
/// Reads an <see cref="ISerialLink"/> through a <see cref="Pipe"/> (background pump, zero polling) and splits the
/// ELM byte stream at its '&gt;' prompt / line terminators.
/// </summary>
internal sealed class ElmChannel : IAsyncDisposable
{
    private readonly ISerialLink _link;
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 18, useSynchronizationContext: false));
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;

    public ElmChannel(ISerialLink link)
    {
        _link = link;
        _pump = Task.Run(PumpAsync);
    }

    private PipeReader Reader => _pipe.Reader;
    public bool Closed { get; private set; }

    private async Task PumpAsync()
    {
        var writer = _pipe.Writer;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var mem = writer.GetMemory(512);
                int n = await _link.ReadAsync(mem, _stop.Token).ConfigureAwait(false);
                if (n <= 0) break;
                writer.Advance(n);
                var fl = await writer.FlushAsync(_stop.Token).ConfigureAwait(false);
                if (fl.IsCompleted) break;
            }
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            await writer.CompleteAsync(e is OperationCanceledException ? null : e).ConfigureAwait(false);
        }
        Closed = true;
    }

    /// <summary>Drops everything currently buffered (stale replies).</summary>
    public void Discard()
    {
        // The pump drains the link continuously, so only the pipe needs flushing.
        while (Reader.TryRead(out var r))
        {
            var end = r.Buffer.End;
            bool done = r.IsCompleted || r.IsCanceled;
            Reader.AdvanceTo(end);
            if (done) break;
            if (r.Buffer.IsEmpty) break;
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _link.WriteAsync(data, ct);

    /// <summary>Result of <see cref="ReadUntilPromptAsync"/>.</summary>
    public readonly record struct PromptRead(string Text, bool TimedOut, bool Closed);

    /// <summary>
    /// Reads until the ELM prompt '&gt;' (consumed, not included) or <paramref name="timeout"/>.
    /// A user cancellation throws; a timeout returns whatever arrived with <c>TimedOut = true</c>.
    /// </summary>
    public async ValueTask<PromptRead> ReadUntilPromptAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var reader = Reader;
        while (true)
        {
            ReadResult rr;
            try
            {
                rr = await reader.ReadAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // timeout: hand back partial text
                string partial = "";
                if (reader.TryRead(out var pr))
                {
                    partial = Decode(pr.Buffer);
                    reader.AdvanceTo(pr.Buffer.End);
                }
                return new PromptRead(partial, true, false);
            }
            var buf = rr.Buffer;
            var pos = FindPrompt(buf);
            if (pos is { } p)
            {
                string text = Decode(buf.Slice(0, p));
                reader.AdvanceTo(buf.GetPosition(1, p));
                return new PromptRead(text, false, false);
            }
            if (rr.IsCompleted)
            {
                string text = Decode(buf);
                reader.AdvanceTo(buf.End);
                return new PromptRead(text, false, true);
            }
            reader.AdvanceTo(buf.Start, buf.End);
        }
    }

    /// <summary>Reads until <paramref name="token"/> appears (consumed together with everything before it). Timeout returns false.</summary>
    public async ValueTask<(bool Found, string Text)> ReadUntilTokenAsync(string token, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var reader = Reader;
        while (true)
        {
            ReadResult rr;
            try { rr = await reader.ReadAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                string partial = "";
                if (reader.TryRead(out var pr)) { partial = Decode(pr.Buffer); reader.AdvanceTo(pr.Buffer.End); }
                return (false, partial);
            }
            var buf = rr.Buffer;
            string text = Decode(buf);
            int idx = text.IndexOf(token, StringComparison.Ordinal);
            if (idx >= 0)
            {
                reader.AdvanceTo(buf.GetPosition(idx + token.Length, buf.Start));
                return (true, text[..(idx + token.Length)]);
            }
            if (rr.IsCompleted) { reader.AdvanceTo(buf.End); return (false, text); }
            reader.AdvanceTo(buf.Start, buf.End);
        }
    }

    /// <summary>
    /// Reads the next line (terminated by CR/LF) or the prompt. Returns (line, false) for a line, (partialOrNull, true) for the prompt,
    /// (null, true, closed) at EOF. Honors only the caller token (no timeout) – used by monitor mode.
    /// </summary>
    public async ValueTask<(string? Line, bool Prompt, bool Closed)> ReadLineOrPromptAsync(CancellationToken ct)
    {
        var reader = Reader;
        while (true)
        {
            var rr = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buf = rr.Buffer;
            // find first of \r \n >
            var seq = new SequenceReader<byte>(buf);
            if (seq.TryReadToAny(out ReadOnlySpan<byte> before, "\r\n>"u8, advancePastDelimiter: false))
            {
                seq.TryPeek(out byte delim);
                string text = Encoding.ASCII.GetString(before);
                if (delim == (byte)'>')
                {
                    reader.AdvanceTo(buf.GetPosition(before.Length + 1, buf.Start));
                    return (text.Length > 0 ? text : null, true, false);
                }
                reader.AdvanceTo(buf.GetPosition(before.Length + 1, buf.Start));
                if (text.Length == 0) continue; // blank line
                return (text, false, false);
            }
            if (rr.IsCompleted)
            {
                reader.AdvanceTo(buf.End);
                return (null, true, true);
            }
            reader.AdvanceTo(buf.Start, buf.End);
        }
    }

    private static SequencePosition? FindPrompt(in ReadOnlySequence<byte> buf)
    {
        if (buf.IsSingleSegment)
        {
            int i = buf.FirstSpan.IndexOf((byte)'>');
            return i < 0 ? null : buf.GetPosition(i);
        }
        return buf.PositionOf((byte)'>');
    }

    private static string Decode(in ReadOnlySequence<byte> seq) =>
        seq.IsSingleSegment ? Encoding.ASCII.GetString(seq.FirstSpan) : Encoding.ASCII.GetString(seq);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _link.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { await _pump.ConfigureAwait(false); } catch { /* ignore */ }
        await Reader.CompleteAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
