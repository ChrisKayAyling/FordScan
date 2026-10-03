using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FordDiag.Core.Coding;

/// <summary>Named-option definitions for one module family (e.g. APIM SYNC 3), loaded from JSON.</summary>
public sealed class CodingDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public uint Module { get; init; }
    /// <summary>Expected data length of block 1, 2, ... Used to recognise the right definition and to refuse mismatching writes.</summary>
    public IReadOnlyList<int>? BlockSizes { get; init; }
    /// <summary>Part number prefixes (e.g. "BC3T") of the modules the definition was written for.</summary>
    public IReadOnlyList<string> PartNumberPrefixes { get; init; } = Array.Empty<string>();
    public string? Source { get; init; }
    public string? License { get; init; }
    public string? Attribution { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<CodingBlock> Blocks { get; init; } = Array.Empty<CodingBlock>();

    public CodingBlock? Block(int block) => Blocks.FirstOrDefault(b => b.Block == block);
    public int FieldCount => Blocks.Sum(b => b.Fields.Count);

    /// <summary>True when every block present in <paramref name="blocks"/> has the length this definition expects.</summary>
    public bool MatchesLayout(IReadOnlyDictionary<int, int> blocks)
    {
        if (BlockSizes is not null)
            return blocks.All(kv => kv.Key >= 1 && kv.Key <= BlockSizes.Count && BlockSizes[kv.Key - 1] == kv.Value);
        // imported definitions know where their options end; the module's block must be at least that long
        var known = Blocks.Where(b => blocks.ContainsKey(b.Block)).ToList();
        return known.Count > 0 && known.All(b => blocks[b.Block] >= b.MinSize);
    }

    /// <summary>True when the layout is exact (every block length is known), false when it is only a lower bound.</summary>
    public bool IsExactLayout => BlockSizes is not null;

    // ---- JSON ----
    private sealed class FieldDto
    {
        public string Name { get; set; } = "";
        public int Byte { get; set; }
        public int Bit { get; set; }
        public int Size { get; set; }
        public string Kind { get; set; } = "enum";
        public string? Note { get; set; }
        public string? Loc { get; set; }
        public Dictionary<string, string>? Options { get; set; }
        public double? Multiplier { get; set; }
        public double? Offset { get; set; }
        public string? Unit { get; set; }
        public double? Min { get; set; }
        public double? Max { get; set; }
    }
    private sealed class BlockDto { public int Block { get; set; } public List<FieldDto> Fields { get; set; } = new(); public List<int>? LineBytes { get; set; } public int MinSize { get; set; } public bool LayoutGuessed { get; set; } }
    private sealed class DefDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Module { get; set; } = "";
        public List<int>? BlockSizes { get; set; }
        public List<string>? PartNumberPrefixes { get; set; }
        public string? Source { get; set; }
        public string? License { get; set; }
        public string? Attribution { get; set; }
        public string? Notes { get; set; }
        public List<BlockDto> Blocks { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static CodingDefinition FromJson(string json)
    {
        var d = JsonSerializer.Deserialize<DefDto>(json, JsonOpts) ?? throw new InvalidDataException("Empty definition.");
        if (d.Id.Length == 0 || d.Name.Length == 0) throw new InvalidDataException("A definition needs an id and a name.");
        if (!FordModules.TryParseAddress(d.Module, out var module)) throw new InvalidDataException($"Bad module address '{d.Module}' (expected e.g. 726).");
        var blocks = d.Blocks.Select(b => new CodingBlock(b.Block, b.Fields.Select(f => ToField(f)).Where(f => f is not null).Select(f => f!).ToList(), b.LineBytes, b.MinSize, b.LayoutGuessed)).ToList();
        return new CodingDefinition
        {
            Id = d.Id, Name = d.Name, Module = module, BlockSizes = d.BlockSizes, PartNumberPrefixes = d.PartNumberPrefixes ?? new List<string>(), Source = d.Source, License = d.License,
            Attribution = d.Attribution, Notes = d.Notes, Blocks = blocks,
        };
    }

    private static CodingField? ToField(FieldDto f)
    {
        var kind = f.Kind.ToLowerInvariant() switch { "enum" or "flag" => FieldKind.Enum, "value" => FieldKind.Value, "ascii" => FieldKind.Ascii, _ => (FieldKind?)null };
        if (kind is null || f.Size <= 0 || f.Size > 32 || f.Byte < 0) return null;
        var opts = new Dictionary<long, string>();
        foreach (var (k, v) in f.Options ?? new()) if (long.TryParse(k, out var n)) opts[n] = v;
        var field = new CodingField
        {
            Name = f.Name, Byte = f.Byte, Bit = f.Bit, Size = f.Size, Kind = kind.Value, Options = opts,
            Multiplier = f.Multiplier ?? 1, Offset = f.Offset ?? 0, Unit = f.Unit, Min = f.Min, Max = f.Max, Note = f.Note, Loc = f.Loc,
        };
        return field.Bit is >= 0 and < 8 ? field : null;
    }
}

public static class DefinitionLibrary
{
    private static readonly Lazy<IReadOnlyList<CodingDefinition>> BuiltIn = new(() =>
    {
        var asm = typeof(DefinitionLibrary).Assembly;
        var list = new List<CodingDefinition>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("FordDiag.Definitions.", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            list.Add(CodingDefinition.FromJson(r.ReadToEnd()));
        }
        return list;
    });

    /// <summary>Built-in definitions plus any *.json in <paramref name="userDirectory"/> (user files win on a duplicate id).</summary>
    public static IReadOnlyList<CodingDefinition> Load(string? userDirectory = null, Action<string>? warn = null)
    {
        var all = BuiltIn.Value.ToList();
        if (userDirectory is not null && Directory.Exists(userDirectory))
            foreach (var f in Directory.GetFiles(userDirectory, "*.json"))
            {
                try
                {
                    var d = CodingDefinition.FromJson(File.ReadAllText(f));
                    all.RemoveAll(x => x.Id == d.Id);
                    all.Add(d);
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { warn?.Invoke($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
        return all;
    }

    /// <summary>Best definition for a module whose blocks have the given lengths, or null when none fits.</summary>
    public static CodingDefinition? Find(IReadOnlyList<CodingDefinition> library, uint module, IReadOnlyDictionary<int, int> blockLengths, IEnumerable<string>? partNumbers = null) =>
        Candidates(library, module, blockLengths, partNumbers).FirstOrDefault();

    /// <summary>
    /// Every definition that fits the module, best first: part number prefix match, then exact layouts, then the one that
    /// documents the most options. Several fit when a module exists in generations with identical block sizes, so callers should
    /// let the user choose.
    /// </summary>
    public static IReadOnlyList<CodingDefinition> Candidates(IReadOnlyList<CodingDefinition> library, uint module, IReadOnlyDictionary<int, int> blockLengths, IEnumerable<string>? partNumbers = null)
    {
        var prefixes = (partNumbers ?? Array.Empty<string>()).Select(PartPrefix).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return library.Where(d => d.Module == module && d.MatchesLayout(blockLengths))
                      .OrderByDescending(d => d.PartNumberPrefixes.Any(prefixes.Contains))
                      .ThenByDescending(d => d.IsExactLayout)
                      .ThenByDescending(d => d.FieldCount)
                      .ToList();
    }

    /// <summary>"AB5T-14B476-DA" -> "AB5T".</summary>
    public static string PartPrefix(string partNumber)
    {
        var i = partNumber.IndexOf('-');
        return (i > 0 ? partNumber[..i] : partNumber).Trim();
    }
}
