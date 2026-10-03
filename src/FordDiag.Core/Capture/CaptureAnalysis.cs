using System.Text.Json;
using System.Text.Json.Serialization;
using FordDiag.Comms;
using FordDiag.Core.Coding;
using FordDiag.Core.Service;

namespace FordDiag.Core.Capture;

public sealed record CaptureSummary(int Exchanges, int Failed, IReadOnlyList<(uint Module, int Count)> Modules, IReadOnlyDictionary<string, int> Services,
    string? Vin, bool SecurityAccess, bool Programming, int Writes);

public sealed record DidSample(uint Module, ushort Did, int Length, int Count, IReadOnlyList<string> Samples);

public static class CaptureAnalysis
{
    public static CaptureSummary Summarize(DecodedCapture c)
    {
        var x = c.Exchanges;
        var mods = x.GroupBy(e => e.TxId).Select(g => (g.Key, g.Count())).OrderByDescending(m => m.Item2).ToList();
        var services = x.GroupBy(e => UdsAnnotator.ServiceName(e.Service)).ToDictionary(g => g.Key, g => g.Count());
        return new CaptureSummary(x.Count, x.Count(e => e.Response is null || e.Negative), mods, services, FindVin(c),
            x.Any(UdsAnnotator.IsSecurity), x.Any(UdsAnnotator.IsProgramming), x.Count(e => e.Positive && e.Service is 0x2E or 0x2F or 0x31));
    }

    public static string? FindVin(DecodedCapture c)
    {
        foreach (var e in c.Exchanges.Where(e => e.Request.Length == 3 && e.Request[0] == 0x22 && e.Request[1] == 0xF1 && e.Request[2] == 0x90 && e.Response is { Length: 20 }))
        {
            var s = System.Text.Encoding.ASCII.GetString(e.Response!, 3, 17);
            if (s.All(char.IsAsciiLetterOrDigit)) return s;
        }
        return null;
    }

    /// <summary>The exchanges between two markers (segment 0 is everything before the first marker).</summary>
    public static IReadOnlyList<UdsExchange> Segment(DecodedCapture c, int segment) => c.Exchanges.Where(e => e.Segment == segment).ToList();

    public static IEnumerable<DidSample> DidInventory(DecodedCapture c) =>
        c.Exchanges.Where(e => e.Request.Length == 3 && e.Request[0] == 0x22 && e.Response is { Length: > 3 } r && r[0] == 0x62)
            .GroupBy(e => (e.TxId, Did: (ushort)(e.Request[1] << 8 | e.Request[2])))
            .Select(g => new DidSample(g.Key.TxId, g.Key.Did, g.First().Response!.Length - 3, g.Count(),
                g.Select(e => Hex.ToString(e.Response.AsSpan(3), true)).Distinct().Take(4).ToList()))
            .OrderBy(d => d.Module).ThenBy(d => d.Did);

    /// <summary>Configuration blocks (DE00...) that the captured program read or wrote, newest value per block.</summary>
    public static AsBuiltImage AsBuiltFrom(DecodedCapture c, uint module)
    {
        var img = new AsBuiltImage { Module = module };
        foreach (var e in c.Exchanges.Where(e => e.TxId == module))
        {
            if (e.Request.Length == 3 && e.Request[0] == 0x22 && e.Request[1] == 0xDE && e.Response is { Length: > 3 } r && r[0] == 0x62)
                img.Blocks[e.Request[2] + 1] = r.AsSpan(3).ToArray();
            else if (e.Request.Length > 3 && e.Request[0] == 0x2E && e.Request[1] == 0xDE && e.Positive)
                img.Blocks[e.Request[2] + 1] = e.Request.AsSpan(3).ToArray();
        }
        return img;
    }

    /// <summary>
    /// Drafts a procedure from the exchanges of one section of a capture. Tester present, reads and failed requests are left out; a
    /// closing session request and output-control hand-backs become cleanup steps; long gaps become hold times.
    /// </summary>
    public static Procedure BuildProcedure(IReadOnlyList<UdsExchange> segment, string name, string? vin = null, ProcedureKind kind = ProcedureKind.Service)
    {
        var interesting = segment.Where(e => e.Service is 0x10 or 0x11 or 0x14 or 0x27 or 0x28 or 0x2E or 0x2F or 0x31 or 0x85 or 0x3D or 0x08 && e.Positive).ToList();
        if (interesting.Count == 0) throw new InvalidOperationException("No service, routine or output-control requests in this section.");
        uint module = interesting.GroupBy(e => e.TxId).OrderByDescending(g => g.Count(x => x.Service is 0x2E or 0x2F or 0x31)).First().Key;
        var mine = interesting.Where(e => e.TxId == module).ToList();

        var warnings = new List<string>();
        if (mine.Any(UdsAnnotator.IsSecurity)) warnings.Add("The captured run used security access (0x27). The key depends on a seed that changes every time, so the steps after it will probably be refused. This tool cannot calculate keys.");
        if (mine.Any(UdsAnnotator.IsProgramming)) warnings.Add("The capture contains programming (flashing) requests. Do not replay them.");
        if (mine.Any(e => e.Service == 0x2E)) warnings.Add("This procedure writes data identifiers. Make a backup first.");

        var steps = new List<ProcedureStep>();
        var cleanup = new List<ProcedureStep>();
        for (int i = 0; i < mine.Count; i++)
        {
            var e = mine[i];
            if (e.Service == 0x27) continue;
            double gap = i + 1 < mine.Count ? mine[i + 1].Seconds - e.Seconds : 0;
            bool isReturn = e.Service == 0x2F && e.Request.Length >= 4 && e.Request[3] == 0x00;
            bool isDefaultSession = e.Service == 0x10 && e.Request.Length > 1 && e.Request[1] == 0x01;
            var step = new ProcedureStep(e.RequestHex, ExpectFor(e), UdsAnnotator.Describe(e).Split(" → ")[0], gap >= 1.5 && !isReturn ? Math.Round(gap) : 0);
            if (isReturn || isDefaultSession && i == mine.Count - 1 || isDefaultSession && cleanup.Count > 0) cleanup.Add(step);
            else if (cleanup.Count > 0) cleanup.Add(step);
            else steps.Add(step);
        }
        // anything after the first hand-back belongs to the cleanup; make sure a default-session request is last
        var m = FordModules.Find(module.ToString("X3"));
        string id = "captured-" + new string(name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return new Procedure
        {
            Id = id, Name = name, Kind = kind, Category = "Captured", Module = module,
            Description = $"Recorded from a real run on module {m?.Abbrev ?? module.ToString("X3")} ({module:X3}){(vin is null ? "" : $", VIN {vin}")}. Check each step before replaying it.",
            Warnings = warnings, Steps = steps, Cleanup = cleanup,
            Conditions = new[] { "Same vehicle type as the capture", "Battery charger connected" },
            Source = $"Captured with FordDiag on {DateTime.Now:yyyy-MM-dd}", Confidence = "captured",
        };
    }

    private static string? ExpectFor(UdsExchange e)
    {
        if (e.Response is not { Length: > 0 } r) return null;
        int n = e.Service switch { 0x31 => 4, 0x2F => 4, 0x2E => 3, 0x10 or 0x11 or 0x28 or 0x85 => 2, _ => 1 };
        return Hex.ToString(r.AsSpan(0, Math.Min(n, r.Length)), true);
    }

    // ---- procedure files ----
    public static string ToJson(IEnumerable<Procedure> procedures)
    {
        object Step(ProcedureStep s) => new { hex = s.Hex, expect = s.Expect, desc = s.Description, hold = s.HoldSeconds > 0 ? s.HoldSeconds : (double?)null };
        var list = procedures.Select(p => new
        {
            id = p.Id, name = p.Name, kind = p.Kind == ProcedureKind.OutputTest ? "outputTest" : "service", category = p.Category,
            module = p.Module?.ToString("X3"), description = p.Description, conditions = p.Conditions, warnings = p.Warnings,
            steps = p.Steps.Select(Step), cleanup = p.Cleanup.Select(Step), source = p.Source, confidence = p.Confidence, minVoltage = p.MinVoltage,
        });
        return JsonSerializer.Serialize(new { procedures = list }, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    }

    /// <summary>A mode 22 set skeleton from the DIDs seen in a capture: lengths are real, scaling is unknown and must be filled in.</summary>
    public static string PidSetDraft(DecodedCapture c, string name)
    {
        var commands = DidInventory(c).Select(d => new
        {
            hdr = d.Module.ToString("X3"), did = d.Did.ToString("X4"), freq = 5,
            signals = new[] { new { id = $"{d.Module:X3}_{d.Did:X4}", name = $"DID {d.Did:X4} ({UdsAnnotatorModule(d.Module)})", len = d.Length * 8, max = Math.Pow(2, Math.Min(d.Length * 8, 31)) - 1, unit = "scalar", note = "Scaling unknown. Samples: " + string.Join(" | ", d.Samples) } },
        });
        return JsonSerializer.Serialize(new { id = "captured-" + name, name, repo = "Ford", source = "captured", license = "yours", commands }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string UdsAnnotatorModule(uint m) => FordModules.Find(m.ToString("X3"))?.Abbrev ?? m.ToString("X3");
}
