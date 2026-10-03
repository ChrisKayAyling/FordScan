using System.Text;
using System.Text.Json;

namespace FordDiag.Core.Capture;

/// <summary>
/// One line of adapter traffic. Direction: '&gt;' a command sent to the adapter, '&lt;' a line the adapter answered,
/// 'P' the adapter prompt (reply complete), '#' a marker typed by the user, 'i' information.
/// </summary>
public sealed record CaptureEvent(long Ms, char Dir, string Text)
{
    public double Seconds => Ms / 1000.0;
}

/// <summary>Thread-safe record of ELM327/STN conversations between a program such as FORScan and an adapter.</summary>
public sealed class CaptureLog
{
    private readonly List<CaptureEvent> _events = new();
    private readonly object _lock = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public event Action<CaptureEvent>? Added;
    public int Count { get { lock (_lock) return _events.Count; } }

    public CaptureEvent Add(char dir, string text)
    {
        CaptureEvent e;
        lock (_lock) { e = new CaptureEvent(_clock.ElapsedMilliseconds, dir, text); _events.Add(e); }
        Added?.Invoke(e);
        return e;
    }

    public void Mark(string label) => Add('#', label);

    public IReadOnlyList<CaptureEvent> Snapshot() { lock (_lock) return _events.ToArray(); }

    public void Save(string path)
    {
        var sb = new StringBuilder();
        foreach (var e in Snapshot()) sb.AppendLine(JsonSerializer.Serialize(new { ms = e.Ms, d = e.Dir.ToString(), s = e.Text }));
        File.WriteAllText(path, sb.ToString());
    }

    public static IReadOnlyList<CaptureEvent> Load(string path)
    {
        var list = new List<CaptureEvent>();
        foreach (var line in File.ReadLines(path).Where(l => l.Trim().Length > 0))
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            list.Add(new CaptureEvent(r.GetProperty("ms").GetInt64(), r.GetProperty("d").GetString()![0], r.GetProperty("s").GetString() ?? ""));
        }
        return list;
    }

    /// <summary>
    /// Reads a plain text trace as written by many programs and by the Terminal page: lines starting with "&gt;" are commands,
    /// "&lt;" are replies, other non-empty lines are replies as well. A prompt is inserted before the next command.
    /// </summary>
    public static IReadOnlyList<CaptureEvent> FromText(string text)
    {
        var list = new List<CaptureEvent>();
        long ms = 0;
        bool open = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            if (line.StartsWith('>') && line.Length > 1)
            {
                if (open) list.Add(new CaptureEvent(ms, 'P', ""));
                list.Add(new CaptureEvent(ms += 10, '>', line[1..].Trim())); open = true;
            }
            else if (line.StartsWith('<')) list.Add(new CaptureEvent(ms += 10, '<', line[1..].Trim()));
            else if (line.StartsWith('#')) list.Add(new CaptureEvent(ms, '#', line[1..].Trim()));
            else list.Add(new CaptureEvent(ms += 10, '<', line));
        }
        if (open) list.Add(new CaptureEvent(ms, 'P', ""));
        return list;
    }
}

/// <summary>Turns a byte stream into lines (CR or LF terminated); the ELM prompt character is reported separately.</summary>
public sealed class LineSplitter
{
    private readonly StringBuilder _sb = new();
    private readonly bool _prompt;
    public LineSplitter(bool reportPrompt) => _prompt = reportPrompt;

    public IEnumerable<(char Kind, string Text)> Feed(ReadOnlySpan<byte> bytes)
    {
        var result = new List<(char, string)>();
        foreach (var b in bytes)
        {
            char c = (char)b;
            if (c is '\r' or '\n') { if (_sb.Length > 0) { result.Add(('L', _sb.ToString())); _sb.Clear(); } }
            else if (c == '>' && _prompt) { if (_sb.Length > 0) { result.Add(('L', _sb.ToString())); _sb.Clear(); } result.Add(('P', "")); }
            else if (c >= ' ' || c == '\t') _sb.Append(c);
        }
        return result;
    }
}
