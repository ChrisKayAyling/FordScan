using System.Text.Json;
using System.Globalization;

namespace FordDiag.Core.Live;

/// <summary>
/// One value inside the answer to a Mode 22 request. Decoding follows the OBDb reference implementation: <see cref="Bix"/>
/// is the bit index (MSB first) into the data that follows "62 DID"; value = raw * Mul / Div + Add, clamped to [Min, Max]
/// when Max &gt; Min; values at or beyond NullMin/NullMax mean "no value".
/// </summary>
public sealed class PidSignal
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Group { get; init; } = "";
    public int Bix { get; init; }
    public int Len { get; init; }
    public double Mul { get; init; } = 1;
    public double Div { get; init; } = 1;
    public double Add { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? NullMin { get; init; }
    public double? NullMax { get; init; }
    public string Unit { get; init; } = "";
    public bool Signed { get; init; }
    public bool ByteSwapped { get; init; }
    public IReadOnlyDictionary<long, string>? Map { get; init; }
    public string? Note { get; init; }

    public int RequiredBytes => (Bix + Len + 7) / 8;
    public bool IsEnum => Map is { Count: > 0 };

    public long Raw(ReadOnlySpan<byte> data)
    {
        if (Len is <= 0 or > 62 || data.Length < RequiredBytes) throw new ArgumentException($"{Name}: need {RequiredBytes} data bytes, got {data.Length}.");
        Span<byte> d = stackalloc byte[data.Length];
        data.CopyTo(d);
        if (ByteSwapped && Len > 8) d.Slice(Bix / 8, (Len + 7) / 8).Reverse();
        long v = 0;
        for (int i = Bix; i < Bix + Len; i++) v = v << 1 | (uint)(d[i / 8] >> (7 - i % 8) & 1);
        if (Signed && (v & 1L << (Len - 1)) != 0) v -= 1L << Len;
        return v;
    }

    /// <summary>Scaled value, or null when the signal reports "no value".</summary>
    public double? Decode(ReadOnlySpan<byte> data)
    {
        double v = Raw(data) * Mul / Div + Add;
        if (Max is double max && max > (Min ?? 0)) v = Math.Max(Min ?? 0, Math.Min(v, max));
        if (NullMin is double nmin && v <= nmin + double.Epsilon) return null;
        if (NullMax is double nmax && v >= nmax - double.Epsilon) return null;
        return v;
    }

    /// <summary>Writes the raw value that corresponds to <paramref name="value"/> into <paramref name="data"/> (used by the simulator).</summary>
    public void Encode(Span<byte> data, double value)
    {
        long raw = (long)Math.Round((value - Add) * Div / Mul);
        if (Signed && raw < 0) raw += 1L << Len;
        raw &= (1L << Len) - 1;
        for (int i = 0; i < Len; i++)
        {
            int bit = (int)(raw >> (Len - 1 - i) & 1), pos = Bix + i;
            if (bit == 1) data[pos / 8] |= (byte)(1 << (7 - pos % 8)); else data[pos / 8] &= (byte)~(1 << (7 - pos % 8));
        }
        if (ByteSwapped && Len > 8) data.Slice(Bix / 8, (Len + 7) / 8).Reverse();
    }

    public string Format(ReadOnlySpan<byte> data)
    {
        if (IsEnum && Map!.TryGetValue(Raw(data), out var label)) return label;
        var v = Decode(data);
        if (v is null) return "n/a";
        if (Unit == "noyes") return v != 0 ? "Yes" : "No";
        if (Unit == "offon") return v != 0 ? "On" : "Off";
        return v.Value.ToString(Math.Abs(v.Value) >= 1000 || v.Value == Math.Floor(v.Value) ? "0" : "0.##", CultureInfo.InvariantCulture);
    }

    public string UnitSymbol => PidUnits.Symbol(Unit);
}

public static class PidUnits
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["celsius"] = "°C", ["fahrenheit"] = "°F", ["kilometers"] = "km", ["miles"] = "mi", ["kilometersPerHour"] = "km/h", ["milesPerHour"] = "mph",
        ["percent"] = "%", ["volts"] = "V", ["millivolts"] = "mV", ["amps"] = "A", ["kilopascal"] = "kPa", ["pascal"] = "Pa", ["bar"] = "bar", ["psi"] = "psi",
        ["rpm"] = "rpm", ["degrees"] = "°", ["seconds"] = "s", ["minutes"] = "min", ["hours"] = "h", ["milliseconds"] = "ms", ["liters"] = "L", ["gallons"] = "gal",
        ["newtonMeters"] = "Nm", ["watts"] = "W", ["kilowatts"] = "kW", ["kilowattHours"] = "kWh", ["gramsPerSecond"] = "g/s", ["lambda"] = "λ",
        ["g"] = "g", ["gravity"] = "g", ["noyes"] = "", ["offon"] = "", ["scalar"] = "", ["unknown"] = "", ["ratio"] = "", ["milliamps"] = "mA", ["kiloohms"] = "kΩ", ["ampereHours"] = "Ah", ["litersPerHour"] = "L/h", ["hertz"] = "Hz", ["ohms"] = "Ω", ["meters"] = "m", ["cc"] = "cc", ["ampHours"] = "Ah",
    };
    public static string Symbol(string unit) => Map.TryGetValue(unit, out var s) ? s : unit;
}

public sealed class PidCommand
{
    public uint Header { get; init; }
    public ushort Did { get; init; }
    public double FrequencySeconds { get; init; } = 5;
    public int? FromYear { get; init; }
    public int? ToYear { get; init; }
    /// <summary>Marked as unverified by the source project.</summary>
    public bool Experimental { get; init; }
    public IReadOnlyList<PidSignal> Signals { get; init; } = Array.Empty<PidSignal>();

    public int DataLength => Signals.Count == 0 ? 0 : Signals.Max(s => s.RequiredBytes);
    public bool AppliesTo(int? year) => year is not int y || (FromYear is not int f || y >= f) && (ToYear is not int t || y <= t);
    public byte[] Request => new byte[] { 0x22, (byte)(Did >> 8), (byte)Did };
}

/// <summary>The signals known for one vehicle model (optionally only for some model years).</summary>
public sealed class PidSet
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Repo { get; init; } = "";
    public string? Source { get; init; }
    public string? License { get; init; }
    public string? Attribution { get; init; }
    public int? FromYear { get; init; }
    public int? ToYear { get; init; }
    public IReadOnlyList<PidCommand> Commands { get; init; } = Array.Empty<PidCommand>();

    public override string ToString() => $"{Name}  ·  {SignalCount} values";
    public int SignalCount => Commands.Sum(c => c.Signals.Count);
    public bool AppliesTo(int? year) => year is not int y || (FromYear is not int f || y >= f) && (ToYear is not int t || y <= t);
    public IEnumerable<uint> Modules => Commands.Select(c => c.Header).Distinct().Order();
}

public static class PidCatalog
{
    private static readonly Lazy<IReadOnlyList<PidSet>> BuiltIn = new(() =>
    {
        var asm = typeof(PidCatalog).Assembly;
        var list = new List<PidSet>();
        foreach (var n in asm.GetManifestResourceNames().Where(n => n.StartsWith("FordDiag.Pids.", StringComparison.Ordinal)).Order())
        {
            using var s = asm.GetManifestResourceStream(n)!;
            using var r = new StreamReader(s);
            list.Add(FromJson(r.ReadToEnd()));
        }
        return list;
    });

    public static IReadOnlyList<PidSet> Load(string? userDirectory = null, Action<string>? warn = null)
    {
        var all = BuiltIn.Value.ToList();
        if (userDirectory is not null && Directory.Exists(userDirectory))
            foreach (var f in Directory.GetFiles(userDirectory, "*.json"))
            {
                try { var d = FromJson(File.ReadAllText(f)); all.RemoveAll(x => x.Id == d.Id); all.Add(d); }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { warn?.Invoke($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
        return all;
    }

    /// <summary>
    /// Sets for a model, best first: the model's own sets (matching the year), then the generic Ford set.
    /// <paramref name="model"/> is as reported by the decoder ("F-150", "Mustang", "Focus").
    /// </summary>
    public static IReadOnlyList<PidSet> SetsFor(IReadOnlyList<PidSet> all, string? model, int? year)
    {
        string key = Norm(model);
        var own = key.Length == 0 ? new List<PidSet>() : all.Where(s => s.Repo.StartsWith("Ford-", StringComparison.Ordinal) && Norm(s.Repo[5..]) == key && s.AppliesTo(year))
            .OrderByDescending(s => s.FromYear.HasValue || s.ToYear.HasValue).ThenByDescending(s => s.SignalCount).ToList();
        var generic = all.Where(s => s.Repo == "Ford" && s.AppliesTo(year));
        return own.Concat(generic).ToList();
    }

    public static string Norm(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // ---- JSON ----
    private sealed class SigDto
    {
        public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? Group { get; set; }
        public int Len { get; set; } public int Bix { get; set; }
        public double? Mul { get; set; } public double? Div { get; set; } public double? Add { get; set; }
        public double? Min { get; set; } public double? Max { get; set; } public double? NullMin { get; set; } public double? NullMax { get; set; }
        public string? Unit { get; set; } public bool Signed { get; set; } public bool Blsb { get; set; }
        public Dictionary<string, string>? Map { get; set; } public string? Note { get; set; }
    }
    private sealed class CmdDto
    {
        public string Hdr { get; set; } = ""; public string Did { get; set; } = ""; public double Freq { get; set; } = 5;
        public int? From { get; set; } public int? To { get; set; } public bool Exp { get; set; }
        public List<SigDto> Signals { get; set; } = new();
    }
    private sealed class SetDto
    {
        public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string? Repo { get; set; }
        public string? Source { get; set; } public string? License { get; set; } public string? Attribution { get; set; }
        public int? FromYear { get; set; } public int? ToYear { get; set; } public List<CmdDto> Commands { get; set; } = new();
    }
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static PidSet FromJson(string json)
    {
        var d = JsonSerializer.Deserialize<SetDto>(json, Opts) ?? throw new InvalidDataException("Empty PID set.");
        if (d.Id.Length == 0 || d.Name.Length == 0) throw new InvalidDataException("A PID set needs an id and a name.");
        var cmds = new List<PidCommand>();
        foreach (var c in d.Commands)
        {
            if (!uint.TryParse(c.Hdr, NumberStyles.HexNumber, null, out var hdr) || !ushort.TryParse(c.Did, NumberStyles.HexNumber, null, out var did)) continue;
            var sigs = c.Signals.Where(s => s.Len is > 0 and <= 62 && s.Bix >= 0).Select(s => new PidSignal
            {
                Id = s.Id, Name = s.Name, Group = s.Group ?? "", Bix = s.Bix, Len = s.Len, Mul = s.Mul ?? 1, Div = s.Div is null or 0 ? 1 : s.Div.Value, Add = s.Add ?? 0,
                Min = s.Min, Max = s.Max, NullMin = s.NullMin, NullMax = s.NullMax, Unit = s.Unit ?? "", Signed = s.Signed, ByteSwapped = s.Blsb, Note = s.Note,
                Map = s.Map?.Where(kv => long.TryParse(kv.Key, out _)).ToDictionary(kv => long.Parse(kv.Key), kv => kv.Value),
            }).ToList();
            if (sigs.Count > 0) cmds.Add(new PidCommand { Header = hdr, Did = did, FrequencySeconds = c.Freq, FromYear = c.From, ToYear = c.To, Experimental = c.Exp, Signals = sigs });
        }
        return new PidSet { Id = d.Id, Name = d.Name, Repo = d.Repo ?? "", Source = d.Source, License = d.License, Attribution = d.Attribution, FromYear = d.FromYear, ToYear = d.ToYear, Commands = cmds };
    }
}
