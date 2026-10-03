using System.Globalization;
using FordDiag.Comms;

namespace FordDiag.Core.Capture;

/// <summary>One diagnostic request and its answer, as seen on the wire.</summary>
public sealed record UdsExchange(double Seconds, uint TxId, uint RxId, byte[] Request, byte[]? Response, string? Error, int PendingCount, double LatencyMs, int Segment)
{
    public byte Service => Request.Length > 0 ? Request[0] : (byte)0;
    public bool Negative => Response is { Length: >= 3 } r && r[0] == 0x7F;
    public bool Positive => Response is { Length: > 0 } r && r[0] == Service + 0x40;
    public string RequestHex => Hex.ToString(Request, true);
    public string ResponseHex => Response is null ? "" : Hex.ToString(Response, true);
}

public sealed record CaptureMarker(double Seconds, string Label);

public sealed class DecodedCapture
{
    public List<UdsExchange> Exchanges { get; } = new();
    public List<CaptureMarker> Markers { get; } = new();
    public List<string> Notes { get; } = new();
    public int ConfigCommands { get; set; }
    public int UnparsedCommands { get; set; }
}

/// <summary>
/// Reconstructs UDS requests and responses from an ELM327/STN command log. It follows the adapter state (ATSH, ATCRA, ATCP, ATCAF,
/// ATH) and understands both manual ISO-TP (frames sent and received as raw CAN data) and automatic formatting (payloads only).
/// </summary>
public static class ElmTrafficDecoder
{
    private sealed class State
    {
        public bool Caf = true, Headers;
        public uint Tx = 0x7DF, Cp = 0x18;
        public uint? Rx;
        public bool Tx29;
    }

    private sealed class Pending
    {
        public double Start; public uint Tx, Rx; public byte[] Request = Array.Empty<byte>(); public int Segment; public int PendingCount;
        public List<byte>? Assembling; public int Expected; public byte[]? Response; public string? Error; public double ResponseAt;
    }

    public static DecodedCapture Decode(IEnumerable<CaptureEvent> events)
    {
        var result = new DecodedCapture();
        var st = new State();
        Pending? cur = null;
        List<byte>? reqAsm = null; int reqExpected = 0;
        int segment = 0;

        void Finish()
        {
            if (cur is null) return;
            result.Exchanges.Add(new UdsExchange(cur.Start, cur.Tx, cur.Rx, cur.Request, cur.Response, cur.Error, cur.PendingCount,
                cur.Response is null ? 0 : Math.Max(0, (cur.ResponseAt - cur.Start) * 1000), cur.Segment));
            cur = null;
        }

        void Begin(double t, byte[] request)
        {
            Finish();
            cur = new Pending { Start = t, Tx = st.Tx, Rx = st.Rx ?? (st.Tx + 8), Request = request, Segment = segment };
        }

        var blocks = SplitBlocks(events, result, ref segment).ToList();
        segment = 0;
        foreach (var b in blocks)
        {
            if (b.Marker is not null) { segment++; continue; }
            var cmd = new string(b.Command.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

            if (cmd.Length == 0) { FeedReplies(b.Replies, b.EndSeconds); continue; }   // a bare CR repeats/polls
            if (cmd.StartsWith("AT") || cmd.StartsWith("ST") && !IsHex(cmd))
            {
                result.ConfigCommands++;
                ApplyConfig(cmd, st);
                continue;
            }
            if (!IsHex(cmd)) { result.UnparsedCommands++; continue; }
            if (cmd.Length % 2 == 1) cmd = cmd[..^1];     // ELM "expected number of responses" digit, e.g. 0322F1901
            if (cmd.Length == 0) { result.UnparsedCommands++; continue; }

            var bytes = Hex.Parse(cmd);
            if (st.Caf)
            {
                Begin(b.StartSeconds, bytes);
            }
            else
            {
                int kind = bytes[0] >> 4;
                if (kind == 0 && bytes.Length > 1)
                {
                    int len = Math.Min(bytes[0] & 0x0F, bytes.Length - 1);
                    Begin(b.StartSeconds, bytes.AsSpan(1, len).ToArray());
                    reqAsm = null;
                }
                else if (kind == 1 && bytes.Length > 2)
                {
                    reqExpected = (bytes[0] & 0x0F) << 8 | bytes[1];
                    reqAsm = bytes.Skip(2).Take(Math.Min(6, reqExpected)).ToList();
                    Begin(b.StartSeconds, Array.Empty<byte>());
                }
                else if (kind == 2 && reqAsm is not null && cur is not null)
                {
                    reqAsm.AddRange(bytes.Skip(1).Take(reqExpected - reqAsm.Count));
                    if (reqAsm.Count >= reqExpected) { cur.Request = reqAsm.ToArray(); reqAsm = null; }
                }
                // kind 3 is the tester's flow control for a response; nothing to record
            }
            FeedReplies(b.Replies, b.EndSeconds);
        }
        Finish();
        return result;

        void FeedReplies(IReadOnlyList<string> lines, double t)
        {
            if (cur is null) return;
            var useFrames = st.Headers || !st.Caf;
            if (useFrames) { foreach (var l in lines) FeedFrame(l, t); }
            else FeedPayloadLines(lines, t);
        }

        void FeedFrame(string line, double t)
        {
            var clean = new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
            if (IsError(clean, out var err)) { if (cur!.Response is null) cur.Error = err; return; }
            uint? id = null;
            if (st.Headers)
            {
                int idLen = st.Tx29 ? 8 : 3;
                if (clean.Length < idLen + 2 || !uint.TryParse(clean[..idLen], NumberStyles.HexNumber, null, out var parsed)) return;
                id = parsed; clean = clean[idLen..];
                if (cur!.Rx != 0 && id != cur.Rx && st.Rx is null && Math.Abs((long)id.Value - (long)cur.Rx) > 0x20) return;   // another module's traffic
            }
            if (clean.Length < 2 || clean.Length % 2 != 0 || !Hex.TryParse(clean, out var f) || f.Length == 0) return;
            if (id is not null) cur!.Rx = id.Value;
            int kind = f[0] >> 4;
            if (kind == 0 && f.Length > 1)
            {
                int len = Math.Min(f[0] & 0x0F, f.Length - 1);
                Complete(f.AsSpan(1, len).ToArray(), t);
            }
            else if (kind == 1 && f.Length > 2)
            {
                cur!.Expected = (f[0] & 0x0F) << 8 | f[1];
                cur.Assembling = f.Skip(2).Take(Math.Min(6, cur.Expected)).ToList();
            }
            else if (kind == 2 && cur!.Assembling is not null)
            {
                cur.Assembling.AddRange(f.Skip(1).Take(cur.Expected - cur.Assembling.Count));
                if (cur.Assembling.Count >= cur.Expected) { var payload = cur.Assembling.ToArray(); cur.Assembling = null; Complete(payload, t); }
            }
        }

        void FeedPayloadLines(IReadOnlyList<string> lines, double t)
        {
            var multi = new SortedDictionary<int, string>();
            foreach (var l in lines)
            {
                var clean = new string(l.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
                if (clean.Length == 0) continue;
                if (IsError(clean, out var err)) { if (cur!.Response is null) cur.Error = err; continue; }
                int colon = clean.IndexOf(':');
                if (colon > 0 && int.TryParse(clean[..colon], NumberStyles.HexNumber, null, out var idx)) { multi[idx] = clean[(colon + 1)..]; continue; }
                if (clean.Length == 3 && clean.All(Uri.IsHexDigit) && lines.Any(x => x.Contains(':'))) continue;   // length line of a multi-line answer
                if (clean.Length % 2 == 0 && Hex.TryParse(clean, out var payload) && payload.Length > 0) Complete(payload, t);
            }
            if (multi.Count > 0 && Hex.TryParse(string.Concat(multi.Values), out var joined) && joined.Length > 0) Complete(joined, t);
        }

        void Complete(byte[] payload, double t)
        {
            if (cur is null) return;
            if (payload.Length >= 3 && payload[0] == 0x7F && payload[2] == 0x78 && cur.Response is null) { cur.PendingCount++; return; }   // response pending
            if (cur.Response is not null) return;      // first answer wins (other modules answering a broadcast are ignored)
            cur.Response = payload; cur.ResponseAt = t; cur.Error = null;
        }
    }

    private static bool IsHex(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);

    private static bool IsError(string clean, out string error)
    {
        foreach (var e in new[] { "NODATA", "CANERROR", "BUFFERFULL", "BUSBUSY", "BUSERROR", "DATAERROR", "FBERROR", "LVRESET", "RXERROR", "STOPPED", "UNABLETOCONNECT", "ACTALERT", "?" })
            if (clean.StartsWith(e)) { error = e switch { "NODATA" => "NO DATA", "CANERROR" => "CAN ERROR", "BUFFERFULL" => "BUFFER FULL", "BUSBUSY" => "BUS BUSY", _ => e }; return true; }
        error = ""; return clean is "OK" or "SEARCHING..." or "SEARCHING";
    }

    private static void ApplyConfig(string cmd, State st)
    {
        string arg(string prefix) => cmd[prefix.Length..];
        if (cmd.StartsWith("ATZ") || cmd.StartsWith("ATD") && !cmd.StartsWith("ATDP") || cmd.StartsWith("ATWS")) { st.Caf = true; st.Headers = false; st.Tx = 0x7DF; st.Rx = null; st.Cp = 0x18; st.Tx29 = false; }
        else if (cmd.StartsWith("ATSH") && uint.TryParse(arg("ATSH"), NumberStyles.HexNumber, null, out var sh))
        {
            int len = arg("ATSH").Length;
            if (len <= 3) { st.Tx = sh; st.Tx29 = false; } else { st.Tx = st.Cp << 24 | sh; st.Tx29 = true; }
            st.Rx = null;
        }
        else if (cmd.StartsWith("ATCP") && uint.TryParse(arg("ATCP"), NumberStyles.HexNumber, null, out var cp)) st.Cp = cp;
        else if (cmd.StartsWith("ATCRA")) st.Rx = uint.TryParse(arg("ATCRA"), NumberStyles.HexNumber, null, out var ra) ? ra : null;
        else if (cmd.StartsWith("ATCAF")) st.Caf = arg("ATCAF") != "0";
        else if (cmd.StartsWith("ATH") && !cmd.StartsWith("ATHS")) st.Headers = arg("ATH") != "0";
    }

    private sealed record Block(string Command, List<string> Replies, double StartSeconds, double EndSeconds, string? Marker);

    private static IEnumerable<Block> SplitBlocks(IEnumerable<CaptureEvent> events, DecodedCapture result, ref int segmentUnused)
    {
        var blocks = new List<Block>();
        string? cmd = null; var replies = new List<string>(); double start = 0, end = 0;
        void Flush() { if (cmd is not null) blocks.Add(new Block(cmd, replies, start, end, null)); cmd = null; replies = new List<string>(); }
        foreach (var e in events)
        {
            switch (e.Dir)
            {
                case '>':
                    Flush(); cmd = e.Text; start = end = e.Seconds; break;
                case '<':
                    if (cmd is null) { cmd = ""; start = e.Seconds; }
                    if (replies.Count == 0 && string.Equals(new string(e.Text.Where(c => !char.IsWhiteSpace(c)).ToArray()), new string((cmd ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()), StringComparison.OrdinalIgnoreCase)) break;   // echo
                    replies.Add(e.Text); end = e.Seconds; break;
                case 'P': end = Math.Max(end, e.Seconds); Flush(); break;
                case '#':
                    Flush(); blocks.Add(new Block("", new List<string>(), e.Seconds, e.Seconds, e.Text));
                    result.Markers.Add(new CaptureMarker(e.Seconds, e.Text)); break;
            }
        }
        Flush();
        return blocks;
    }
}
