using System.Globalization;
using System.Text;
using FordDiag.Comms;

namespace FordDiag.Core;

/// <summary>Key of an as-built line, written "726-01-01": module request address, block, line.</summary>
public readonly record struct AsBuiltKey(uint Module, int Block, int Line) : IComparable<AsBuiltKey>
{
    public override string ToString() => $"{Module:X3}-{Block:X2}-{Line:X2}";
    public int CompareTo(AsBuiltKey o) => (Module, Block, Line).CompareTo((o.Module, o.Block, o.Line));

    public static bool TryParse(string s, out AsBuiltKey key)
    {
        key = default;
        var p = s.Trim().Split('-');
        if (p.Length != 3) return false;
        if (p[0].Length != 3 || !uint.TryParse(p[0], NumberStyles.HexNumber, null, out var m)) return false;
        if (!int.TryParse(p[1], NumberStyles.HexNumber, null, out var b) || !int.TryParse(p[2], NumberStyles.HexNumber, null, out var l)) return false;
        key = new AsBuiltKey(m, b, l);
        return true;
    }
}

public sealed record AsBuiltDifference(AsBuiltKey Key, byte[]? Before, byte[]? After);

/// <summary>
/// Ford "As-Built" configuration text (as printed by Ford service sites and FORScan): one line per
/// <c>MMM-BB-LL hex bytes</c>. Byte widths are not assumed; spaces/colons inside the data are ignored.
/// This class only parses, formats and compares; it never talks to a vehicle.
/// </summary>
public sealed class AsBuiltData
{
    private readonly SortedDictionary<AsBuiltKey, byte[]> _lines = new();

    private static readonly System.Text.RegularExpressions.Regex GFormat = new(@"^([0-9A-Fa-f]{3})G([0-9A-Fa-f])G([0-9A-Fa-f])([0-9A-Fa-f]+)$");

    public IReadOnlyDictionary<AsBuiltKey, byte[]> Lines => _lines;

    public void Set(AsBuiltKey key, byte[] data) => _lines[key] = data;

    /// <summary>Request addresses of the modules that appear in the file.</summary>
    public IReadOnlyList<uint> Modules => _lines.Keys.Select(k => k.Module).Distinct().ToList();

    /// <summary>Lines whose last byte (the checksum) does not match the line contents.</summary>
    public IReadOnlyList<(AsBuiltKey Key, byte Expected, byte Actual)> VerifyChecksums() =>
        _lines.Where(kv => kv.Value.Length >= 2 && AsBuiltChecksum.Compute(kv.Key, kv.Value.AsSpan(0, kv.Value.Length - 1)) != kv.Value[^1])
              .Select(kv => (kv.Key, AsBuiltChecksum.Compute(kv.Key, kv.Value.AsSpan(0, kv.Value.Length - 1)), kv.Value[^1])).ToList();

    /// <summary>Recomputes the last byte of every line (FORScan does the same when you write 00).</summary>
    public int FixChecksums()
    {
        int n = 0;
        foreach (var (k, v) in _lines.ToList())
        {
            if (v.Length < 2) continue;
            var fixedByte = AsBuiltChecksum.Compute(k, v.AsSpan(0, v.Length - 1));
            if (fixedByte != v[^1]) { var c = (byte[])v.Clone(); c[^1] = fixedByte; _lines[k] = c; n++; }
        }
        return n;
    }

    public static AsBuiltData Parse(string text, out IReadOnlyList<string> errors)
    {
        var data = new AsBuiltData();
        var errs = new List<string>();
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//") || line.StartsWith(';')) continue;
            // FORScan ".abt" lines can be written 7D0G5G1<data><checksum> (single hex digit block and line)
            var g = GFormat.Match(line);
            if (g.Success) line = $"{g.Groups[1].Value}-0{g.Groups[2].Value}-0{g.Groups[3].Value} {g.Groups[4].Value}";
            int sp = line.IndexOfAny(new[] { ' ', '\t', ':' });
            if (sp < 0) { errs.Add($"line {n}: missing data"); continue; }
            if (!AsBuiltKey.TryParse(line[..sp], out var key)) { errs.Add($"line {n}: bad address '{line[..sp]}'"); continue; }
            var hex = new string(line[(sp + 1)..].Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray());
            if (hex.Length == 0 || hex.Length % 2 != 0 || !Hex.TryParse(hex, out var bytes)) { errs.Add($"line {n}: bad hex data"); continue; }
            if (!data._lines.TryAdd(key, bytes)) errs.Add($"line {n}: duplicate {key}");
        }
        errors = errs;
        return data;
    }

    public string Format()
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in _lines) sb.Append(k).Append(' ').AppendLine(Hex.ToString(v, true));
        return sb.ToString();
    }

    public static IReadOnlyList<AsBuiltDifference> Diff(AsBuiltData before, AsBuiltData after)
    {
        var keys = new SortedSet<AsBuiltKey>(before._lines.Keys);
        keys.UnionWith(after._lines.Keys);
        var diffs = new List<AsBuiltDifference>();
        foreach (var k in keys)
        {
            before._lines.TryGetValue(k, out var a);
            after._lines.TryGetValue(k, out var b);
            if (a is null || b is null || !a.AsSpan().SequenceEqual(b)) diffs.Add(new AsBuiltDifference(k, a, b));
        }
        return diffs;
    }
}

/// <summary>
/// Per-line checksum: low byte of (module address high byte + low byte + block + line + all data bytes).
/// Derived from the worked example "720-01-01 044A 3464 202F" and verified on 34 lines of a Ford service-site printout;
/// the same rule is used by consp/apim-asbuilt-decode.
/// </summary>
public static class AsBuiltChecksum
{
    public static byte Compute(AsBuiltKey key, ReadOnlySpan<byte> data)
    {
        int sum = (int)(key.Module >> 8) + (int)(key.Module & 0xFF) + key.Block + key.Line;
        foreach (var b in data) sum += b;
        return (byte)sum;
    }
}
