namespace FordDiag.Comms.Elm;

/// <summary>Parsed answer to one adapter command.</summary>
public sealed class ElmReply
{
    public static readonly ElmReply Empty = new("", Array.Empty<string>(), false, null);

    public ElmReply(string raw, IReadOnlyList<string> lines, bool timedOut, string? error)
    {
        Raw = raw; Lines = lines; TimedOut = timedOut; Error = error;
    }

    /// <summary>Everything received before the prompt.</summary>
    public string Raw { get; }

    /// <summary>Non-empty, trimmed, upper-cased lines without the echoed command and "SEARCHING...".</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>The host gave up waiting for the prompt.</summary>
    public bool TimedOut { get; }

    /// <summary>First adapter error line ("NO DATA", "CAN ERROR", "?", ...), or null.</summary>
    public string? Error { get; }

    public bool IsOk => Error is null && !TimedOut && Lines.Any(l => l.Contains("OK", StringComparison.Ordinal));
    public bool NoData => Error == "NO DATA";
    public bool Contains(string text) => Raw.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>All lines joined by '\n'.</summary>
    public string Text => string.Join('\n', Lines);

    public override string ToString() => Text;

    private static readonly string[] ErrorTokens =
    {
        "NO DATA", "CAN ERROR", "BUFFER FULL", "BUS BUSY", "BUS ERROR", "FB ERROR", "DATA ERROR", "RX ERROR", "UNABLE TO CONNECT",
        "STOPPED", "ACT ALERT", "LP ALERT", "LV RESET", "ERR", "BUS INIT: ERROR", "BUS INIT:...ERROR", "BUS INIT: ...ERROR",
    };

    /// <summary>Builds a reply from the raw text between command and prompt.</summary>
    internal static ElmReply Parse(string raw, string command, bool timedOut)
    {
        var lines = new List<string>(4);
        string? error = null;
        string cmdKey = Normalize(command);
        bool echoSkipped = false;
        foreach (var part in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = part.Trim();
            if (line.Length == 0) continue;
            line = line.ToUpperInvariant();
            if (!echoSkipped && cmdKey.Length > 0 && Normalize(line) == cmdKey) { echoSkipped = true; continue; }
            if (line.StartsWith("SEARCHING", StringComparison.Ordinal)) continue;
            if (line.StartsWith('<')) line = line[1..].TrimStart();   // "<DATA ERROR", "<RX ERROR"
            lines.Add(line);
            error ??= ClassifyError(line);
        }
        return new ElmReply(raw, lines, timedOut, error);
    }

    internal static string? ClassifyError(string line)
    {
        if (line == "?") return "?";
        foreach (var t in ErrorTokens)
            if (line.Contains(t, StringComparison.Ordinal))
            {
                // "BUS INIT: OK" is not an error; ERR tokens such as "ERR94" are.
                return t.StartsWith("BUS INIT", StringComparison.Ordinal) ? "BUS INIT: ERROR" : t == "ERR" ? line : t;
            }
        return null;
    }

    private static string Normalize(string s) => s.Replace(" ", "").ToUpperInvariant();
}

/// <summary>Running error counters.</summary>
public sealed class ElmCounters
{
    public int Question, NoData, Timeout, Rx, Can, BufferFull, FrameErrors;
    public int Total => Question + NoData + Timeout + Rx + Can + BufferFull + FrameErrors;
}
