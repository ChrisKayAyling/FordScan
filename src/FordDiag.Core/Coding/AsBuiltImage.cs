using System.Xml.Linq;
using FordDiag.Comms;

namespace FordDiag.Core.Coding;

/// <summary>A changed field (or an unnamed byte difference when <see cref="Field"/> is null).</summary>
public sealed record BlockChange(int Block, CodingField? Field, long OldRaw, long NewRaw, int Byte, byte OldByte, byte NewByte)
{
    public string Describe() => Field is null
        ? $"Block {Block} byte {Byte}: {OldByte:X2} -> {NewByte:X2}"
        : $"{Field.Name}: {Field.Describe(OldRaw)} -> {Field.Describe(NewRaw)}";
}

/// <summary>
/// The configuration of one module as data blocks (block N = DID DE00+N-1). A block is the concatenation of the 5-byte
/// payloads of its as-built lines (checksums excluded), which is also the layout of the module's DID.
/// </summary>
public sealed class AsBuiltImage
{
    public const int LineBytes = 5;

    public uint Module { get; init; }
    public SortedDictionary<int, byte[]> Blocks { get; } = new();
    /// <summary>Data bytes per as-built line, per block. Lines are not always 5 bytes wide (BCM: 3-4, SCCM: 4); unknown blocks use <see cref="LineBytes"/>.</summary>
    public SortedDictionary<int, int[]> LineWidths { get; } = new();

    public AsBuiltImage Clone()
    {
        var c = new AsBuiltImage { Module = Module };
        foreach (var (k, v) in Blocks) c.Blocks[k] = (byte[])v.Clone();
        foreach (var (k, v) in LineWidths) c.LineWidths[k] = (int[])v.Clone();
        return c;
    }

    public IReadOnlyDictionary<int, int> BlockLengths => Blocks.ToDictionary(kv => kv.Key, kv => kv.Value.Length);

    /// <summary>Builds the image of one module from as-built lines. The last byte of every line is its checksum.</summary>
    public static AsBuiltImage FromLines(AsBuiltData data, uint module, out IReadOnlyList<string> warnings)
    {
        var warn = new List<string>();
        var img = new AsBuiltImage { Module = module };
        foreach (var grp in data.Lines.Where(l => l.Key.Module == module).GroupBy(l => l.Key.Block))
        {
            var bytes = new List<byte>();
            var widths = new List<int>();
            int expectedLine = 1;
            foreach (var (key, v) in grp.OrderBy(l => l.Key.Line))
            {
                if (key.Line != expectedLine) warn.Add($"{key}: line missing before this one (block data will be shifted)");
                expectedLine = key.Line + 1;
                if (v.Length < 2) { warn.Add($"{key}: no data"); continue; }
                bytes.AddRange(v.AsSpan(0, v.Length - 1).ToArray());
                widths.Add(v.Length - 1);
            }
            img.Blocks[grp.Key] = bytes.ToArray();
            img.LineWidths[grp.Key] = widths.ToArray();
        }
        foreach (var (key, exp, act) in data.VerifyChecksums().Where(c => c.Key.Module == module))
            warn.Add($"{key}: checksum {act:X2} should be {exp:X2} (fixed automatically when saved)");
        warnings = warn;
        return img;
    }

    public static IReadOnlyDictionary<uint, AsBuiltImage> FromFile(AsBuiltData data, out IReadOnlyList<string> warnings)
    {
        var all = new List<string>();
        var map = new Dictionary<uint, AsBuiltImage>();
        foreach (var m in data.Modules) { map[m] = FromLines(data, m, out var w); all.AddRange(w); }
        warnings = all;
        return map;
    }

    /// <summary>Lines with checksums, ready to save or print: <c>MMM-BB-LL data... checksum</c>.</summary>
    public AsBuiltData ToLines()
    {
        var d = new AsBuiltData();
        foreach (var (block, bytes) in Blocks)
        {
            LineWidths.TryGetValue(block, out var widths);
            for (int off = 0, line = 1; off < bytes.Length; line++)
            {
                int w = widths is { Length: > 0 } ? widths[Math.Min(line, widths.Length) - 1] : LineBytes;
                if (w <= 0) w = LineBytes;
                var chunk = bytes.AsSpan(off, Math.Min(w, bytes.Length - off));
                off += w;
                var key = new AsBuiltKey(Module, block, line);
                var data = new byte[chunk.Length + 1];
                chunk.CopyTo(data);
                data[^1] = AsBuiltChecksum.Compute(key, chunk);
                d.Set(key, data);
            }
        }
        return d;
    }

    /// <summary>Adopts the line widths a definition documents for blocks that have none (a block read from a module has no lines).</summary>
    public void ApplyLineWidths(CodingDefinition? def)
    {
        if (def is null) return;
        foreach (var b in def.Blocks.Where(b => b.LineBytes is { Count: > 0 }))
            if (Blocks.TryGetValue(b.Block, out var data) && !LineWidths.ContainsKey(b.Block) && b.LineBytes!.Sum() <= data.Length)
                LineWidths[b.Block] = b.LineBytes!.ToArray();
    }

    public string ToText() => ToLines().Format();

    /// <summary>UCDS export: <c>&lt;VEHICLE&gt;&lt;DATA ID="DE00"&gt;hex&lt;/DATA&gt;...</c> (one element per block, any element name with an ID of DExx).</summary>
    public static AsBuiltImage FromUcdsXml(string xml, uint module)
    {
        var img = new AsBuiltImage { Module = module };
        var doc = XDocument.Parse(xml);
        foreach (var el in doc.Descendants().Where(e => (string?)e.Attribute("ID") is { Length: 4 } id && id.StartsWith("DE", StringComparison.OrdinalIgnoreCase)))
        {
            var id = ((string)el.Attribute("ID")!)[2..];
            if (!int.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out var n)) continue;
            if (Hex.TryParse(new string(el.Value.Where(Uri.IsHexDigit).ToArray()), out var bytes)) img.Blocks[n + 1] = bytes;
        }
        return img;
    }

    /// <summary>
    /// Ford service-site XML (".ab"): elements carry a LABEL such as 726-01-01 and CODE children holding hex that includes the
    /// line checksum. The exact schema is not documented; this reads any element with a key-shaped LABEL attribute.
    /// </summary>
    public static AsBuiltData LinesFromFordXml(string xml)
    {
        var d = new AsBuiltData();
        var doc = XDocument.Parse(xml);
        foreach (var el in doc.Descendants())
        {
            if ((string?)el.Attribute("LABEL") is not { } label || !AsBuiltKey.TryParse(label, out var key)) continue;
            var hex = new string(el.Descendants("CODE").SelectMany(c => c.Value).Where(Uri.IsHexDigit).ToArray());
            if (hex.Length >= 4 && hex.Length % 2 == 0 && Hex.TryParse(hex, out var bytes)) d.Set(key, bytes);
        }
        return d;
    }

    /// <summary>Differences to <paramref name="other"/>, named when a definition is given. Blocks present in only one image are ignored.</summary>
    public IReadOnlyList<BlockChange> Compare(AsBuiltImage other, CodingDefinition? def = null)
    {
        var changes = new List<BlockChange>();
        foreach (var (block, a) in Blocks)
        {
            if (!other.Blocks.TryGetValue(block, out var b) || a.Length != b.Length) continue;
            var covered = new bool[a.Length];
            if (def?.Block(block) is { } cb)
                foreach (var f in cb.Fields.Where(f => f.Fits(a.Length)))
                {
                    long x = f.ReadRaw(a), y = f.ReadRaw(b);
                    for (int i = 0; i < f.ByteSpan; i++) covered[f.Byte + i] = true;
                    if (x != y) changes.Add(new BlockChange(block, f, x, y, f.Byte, a[f.Byte], b[f.Byte]));
                }
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i] && !covered[i]) changes.Add(new BlockChange(block, null, a[i], b[i], i, a[i], b[i]));
        }
        return changes;
    }
}
