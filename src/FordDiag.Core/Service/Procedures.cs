using System.Text.Json;
using FordDiag.Comms;

namespace FordDiag.Core.Service;

public enum ProcedureKind { Service, OutputTest }

/// <summary>One request of a procedure, written as hex ("31 01 20 1A").</summary>
public sealed record ProcedureStep(string Hex, string? Expect, string Description, double HoldSeconds = 0)
{
    public byte[] Bytes => FordDiag.Comms.Hex.Parse(Hex.Replace(" ", ""));
    /// <summary>Expected start of the positive answer; default is service id + 0x40.</summary>
    public byte[] ExpectBytes => string.IsNullOrWhiteSpace(Expect) ? new[] { (byte)(Bytes[0] + 0x40) } : FordDiag.Comms.Hex.Parse(Expect.Replace(" ", ""));
}

/// <summary>A guided service function or output test.</summary>
public sealed class Procedure
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ProcedureKind Kind { get; init; }
    public string Category { get; init; } = "";
    /// <summary>Request address of the module the procedure runs on; null = the module chosen by the user.</summary>
    public uint? Module { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Conditions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Manual { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProcedureStep> Steps { get; init; } = Array.Empty<ProcedureStep>();
    /// <summary>Always sent at the end (after success, failure or stop), e.g. returning control of an output to the module.</summary>
    public IReadOnlyList<ProcedureStep> Cleanup { get; init; } = Array.Empty<ProcedureStep>();
    public string? Source { get; init; }
    /// <summary>"standard" (ISO 14229 / SAE J1979), "reported" (community report, not verified here) or "user".</summary>
    public string Confidence { get; init; } = "reported";
    public double MinVoltage { get; init; } = 12.0;
}

public sealed record StepOutcome(string Hex, bool Ok, string Response, string Message);

public sealed record ProcedureResult(bool Success, IReadOnlyList<StepOutcome> Steps, string Message);

public sealed class ProcedureOptions
{
    /// <summary>Nothing is sent when false: the steps are only listed.</summary>
    public bool Commit { get; init; }
    public Func<CancellationToken, ValueTask<double?>>? ReadVoltage { get; init; }
    /// <summary>Tester present is sent this often while holding (default 2 s).</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(2);
    public IProgress<string>? Progress { get; init; }
}

public static class ProcedureRunner
{
    public static async Task<ProcedureResult> RunAsync(ModuleSession module, Procedure p, ProcedureOptions options, CancellationToken ct = default)
    {
        var outcomes = new List<StepOutcome>();
        if (!options.Commit)
        {
            outcomes.AddRange(p.Steps.Select(s => new StepOutcome(s.Hex, true, "", "Not sent (dry run): " + s.Description)));
            return new ProcedureResult(true, outcomes, "Dry run: nothing was sent.");
        }
        if (options.ReadVoltage is { } rv && await rv(ct).ConfigureAwait(false) is double volts && volts < p.MinVoltage)
            return new ProcedureResult(false, outcomes, $"Battery voltage {volts:F1} V is below {p.MinVoltage:F1} V. Connect a charger first.");

        bool ok = true; string message = "Completed.";
        try
        {
            foreach (var step in p.Steps)
            {
                ct.ThrowIfCancellationRequested();
                options.Progress?.Report(step.Description);
                var o = await SendAsync(module, step, ct).ConfigureAwait(false);
                outcomes.Add(o);
                if (!o.Ok) { ok = false; message = $"Step failed: {o.Message}"; break; }
                if (step.HoldSeconds > 0) await HoldAsync(module, step.HoldSeconds, options, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { ok = false; message = "Stopped."; }
        catch (CommsException ex) { ok = false; message = ex.Message; }
        finally
        {
            // cleanup must run even when the user stopped the procedure, so it does not use the cancelled token
            foreach (var step in p.Cleanup)
            {
                try
                {
                    options.Progress?.Report(step.Description);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    outcomes.Add(await SendAsync(module, step, cts.Token).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is CommsException or OperationCanceledException)
                { outcomes.Add(new StepOutcome(step.Hex, false, "", "Cleanup failed: " + ex.Message)); }
            }
        }
        return new ProcedureResult(ok, outcomes, message);
    }

    private static async Task<StepOutcome> SendAsync(ModuleSession module, ProcedureStep step, CancellationToken ct)
    {
        var rsp = await module.RequestAsync(step.Bytes, ct).ConfigureAwait(false);
        string text = FordDiag.Comms.Hex.ToString(rsp, true);
        if (rsp.Length >= 3 && rsp[0] == 0x7F)
            return new StepOutcome(step.Hex, false, text, $"{step.Description}: NRC 0x{rsp[2]:X2} ({NegativeResponses.Describe(rsp[2])})");
        var expect = step.ExpectBytes;
        if (rsp.Length < expect.Length || !rsp.AsSpan(0, expect.Length).SequenceEqual(expect))
            return new StepOutcome(step.Hex, false, text, $"{step.Description}: unexpected answer {text}");
        return new StepOutcome(step.Hex, true, text, step.Description);
    }

    private static async Task HoldAsync(ModuleSession module, double seconds, ProcedureOptions options, CancellationToken ct)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Min(options.KeepAliveInterval.TotalMilliseconds, (end - DateTime.UtcNow).TotalMilliseconds));
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            if (DateTime.UtcNow < end) await module.RequestAsync(new byte[] { 0x3E, 0x00 }, ct).ConfigureAwait(false);
        }
    }
}

public static class ProcedureLibrary
{
    private sealed class StepDto { public string Hex { get; set; } = ""; public string? Expect { get; set; } public string Desc { get; set; } = ""; public double Hold { get; set; } }
    private sealed class ProcDto
    {
        public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string Kind { get; set; } = "service"; public string? Category { get; set; }
        public string? Module { get; set; } public string? Description { get; set; } public List<string>? Conditions { get; set; } public List<string>? Warnings { get; set; }
        public List<string>? Manual { get; set; } public List<StepDto>? Steps { get; set; } public List<StepDto>? Cleanup { get; set; }
        public string? Source { get; set; } public string? Confidence { get; set; } public double? MinVoltage { get; set; }
    }
    private sealed class FileDto { public List<ProcDto> Procedures { get; set; } = new(); }
    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static IReadOnlyList<Procedure> ParseJson(string json)
    {
        var f = JsonSerializer.Deserialize<FileDto>(json, Opts) ?? throw new InvalidDataException("Empty procedure file.");
        return f.Procedures.Select(ToProcedure).ToList();
    }

    private static ProcedureStep ToStep(StepDto s)
    {
        if (!FordDiag.Comms.Hex.TryParse(s.Hex.Replace(" ", ""), out var b) || b.Length == 0) throw new InvalidDataException($"Bad hex '{s.Hex}'.");
        return new ProcedureStep(string.Join(' ', b.Select(x => x.ToString("X2"))), s.Expect, s.Desc, s.Hold);
    }

    private static Procedure ToProcedure(ProcDto d)
    {
        if (d.Id.Length == 0 || d.Name.Length == 0 || d.Steps is not { Count: > 0 }) throw new InvalidDataException("A procedure needs an id, a name and steps.");
        uint? module = null;
        if (!string.IsNullOrWhiteSpace(d.Module))
        {
            if (!FordModules.TryParseAddress(d.Module, out var m)) throw new InvalidDataException($"Bad module address '{d.Module}'.");
            module = m;
        }
        return new Procedure
        {
            Id = d.Id, Name = d.Name, Kind = d.Kind.Equals("outputTest", StringComparison.OrdinalIgnoreCase) ? ProcedureKind.OutputTest : ProcedureKind.Service,
            Category = d.Category ?? "", Module = module, Description = d.Description ?? "", Conditions = d.Conditions ?? new(), Warnings = d.Warnings ?? new(),
            Manual = d.Manual ?? new(), Steps = d.Steps.Select(ToStep).ToList(), Cleanup = (d.Cleanup ?? new()).Select(ToStep).ToList(),
            Source = d.Source, Confidence = d.Confidence ?? "user", MinVoltage = d.MinVoltage ?? 12.0,
        };
    }

    public static IReadOnlyList<Procedure> Load(string? userDirectory = null, Action<string>? warn = null)
    {
        var asm = typeof(ProcedureLibrary).Assembly;
        var all = new List<Procedure>();
        foreach (var n in asm.GetManifestResourceNames().Where(n => n.StartsWith("FordDiag.Service.", StringComparison.Ordinal)).Order())
        {
            using var s = asm.GetManifestResourceStream(n)!;
            using var r = new StreamReader(s);
            all.AddRange(ParseJson(r.ReadToEnd()));
        }
        if (userDirectory is not null && Directory.Exists(userDirectory))
            foreach (var f in Directory.GetFiles(userDirectory, "*.json"))
            {
                try { foreach (var p in ParseJson(File.ReadAllText(f))) { all.RemoveAll(x => x.Id == p.Id); all.Add(p); } }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { warn?.Invoke($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
        return all;
    }
}
