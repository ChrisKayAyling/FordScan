using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Comms.Serial;
using FordDiag.Core;
using FordDiag.Core.Capture;
using FordDiag.Core.Simulation;

const string Usage = """
fordiag - Ford CAN diagnostics (ELM327 / vLinker / OBDLink)

  fordiag ports                          list serial ports
  fordiag adapters                       list supported adapters
  fordiag scan [--full]                  find modules on HS-CAN and MS-CAN
  fordiag info <module>                  read identification DIDs (VIN, part numbers)
  fordiag read <module> <did>            read one DID (hex), e.g. read BCM DE01
  fordiag dtc <module> [--clear]         read (or clear) trouble codes
  fordiag write <module> <did> <hex>     write a DID. Dry run unless --yes; backs up first
  fordiag capture [--listen 35000] [--out file.jsonl]   record another program's (e.g. FORScan) adapter traffic; type a line to add a marker
  fordiag capture decode <file>          show a capture as UDS requests/answers (.jsonl, or a text trace with > and < lines)
  fordiag capture procedures <file> <dir> write a procedure file for every marker section that sends service/routine/output requests
  fordiag asbuilt check <file>           validate an as-built text file (lines and checksums)
  fordiag asbuilt fix <file> [out]       recalculate line checksums (writes <out> or prints)
  fordiag asbuilt decode <file> <module> list named options (e.g. decode car.txt 7D0)
  fordiag asbuilt diff <a> <b>           compare two as-built files

<module> is an abbreviation (PCM, BCM, ...) or a request address (7E0).
Connection:  --sim | --port <name|host:port> --adapter <key> [--baud N]   --bus hs|ms   --trace
""";

try { return await Run(args); }
catch (OperationCanceledException) { return 130; }
catch (Exception ex) when (ex is CommsException or WriteRefusedException or ArgumentException or IOException)
{ Console.Error.WriteLine("error: " + ex.Message); return 1; }

static async Task<int> Run(string[] argv)
{
    var a = new Args(argv);
    var cmd = a.Positional(0);
    switch (cmd)
    {
        case null or "help" or "-h" or "--help": Console.WriteLine(Usage); return cmd is null ? 2 : 0;
        case "ports":
            foreach (var p in PortEnumerator.GetPorts()) Console.WriteLine($"{p.Name,-20} {p.Description}");
            return 0;
        case "adapters":
            foreach (var ad in FordAdapters.All) Console.WriteLine($"{ad.Key,-15} {ad.DisplayName,-30} MS-CAN: {ad.Switching,-9} {ad.Notes}");
            return 0;
        case "asbuilt": return AsBuilt(a);
        case "capture": return await CaptureCommand(a);
    }

    await using var conn = await Connect(a);
    var t = conn.Transport;
    switch (cmd)
    {
        case "scan":
        {
            var opts = new ScanOptions
            {
                FullRange = a.Flag("--full"), Switching = conn.Switching,
                Log = new Progress<string>(m => Console.Error.WriteLine(m)),
                PromptBusSwitch = (bus, _) =>
                {
                    Console.Error.WriteLine($"Set the adapter's HS/MS switch to {bus.Label()}, then press Enter.");
                    Console.ReadLine();
                    return Task.CompletedTask;
                },
            };
            int n = 0;
            await foreach (var m in FordScanner.ScanAsync(t, opts))
            {
                n++;
                Console.WriteLine($"{m.Bus,-6} {m.Module}{(m.Negative ? "  (answered with negative response)" : "")}");
            }
            Console.WriteLine($"{n} module(s) found.");
            return n > 0 ? 0 : 1;
        }
        case "info":
        {
            var s = Session(a, t);
            foreach (var (did, name, value) in await s.ReadIdentificationAsync()) Console.WriteLine($"{did:X4}  {name,-32} {value}");
            return 0;
        }
        case "read":
        {
            var s = Session(a, t);
            var did = ParseDid(a.Positional(2));
            var data = await s.ReadDidAsync(did);
            Console.WriteLine($"{did:X4}: {Hex.ToString(data, true)}");
            return 0;
        }
        case "dtc":
        {
            var s = Session(a, t);
            if (a.Flag("--clear")) { await s.ClearDtcsAsync(); Console.WriteLine("Cleared."); return 0; }
            var dtcs = await s.ReadDtcsAsync();
            foreach (var d in dtcs) Console.WriteLine(d);
            Console.WriteLine($"{dtcs.Count} code(s).");
            return 0;
        }
        case "write":
        {
            var s = Session(a, t);
            var did = ParseDid(a.Positional(2));
            if (!Hex.TryParse(a.Positional(3) ?? "", out var value)) throw new ArgumentException("Value must be hex bytes.");
            var res = await DidWriter.WriteAsync(t, s, did, value, new WriteOptions
            {
                Commit = a.Flag("--yes"),
                ReadVoltage = conn.ReadVoltage,
            });
            Console.WriteLine($"before: {Hex.ToString(res.Before, true)}");
            Console.WriteLine($"{(res.Written ? "after: " : "would write:")} {Hex.ToString(res.After, true)}");
            if (res.BackupPath is not null) Console.WriteLine($"backup: {res.BackupPath}");
            Console.WriteLine(res.Message + (res.Written ? "" : " Re-run with --yes to write."));
            return res.Written && res.Message.StartsWith("Written") ? 0 : res.Written ? 3 : 0;
        }
        default: Console.Error.WriteLine($"unknown command '{cmd}'\n\n{Usage}"); return 2;
    }
}

static async Task<int> CaptureCommand(Args a)
{
    switch (a.Positional(1))
    {
        case "decode":
        {
            var d = ElmTrafficDecoder.Decode(LoadCapture(a.Positional(2)));
            PrintDecoded(d);
            return 0;
        }
        case "procedures":
        {
            var d = ElmTrafficDecoder.Decode(LoadCapture(a.Positional(2)));
            var dir = a.Positional(3) ?? throw new ArgumentException("Output folder required.");
            Directory.CreateDirectory(dir);
            return WriteProcedures(d, dir);
        }
    }

    // live recording
    var log = new CaptureLog();
    log.Added += e =>
    {
        if (e.Dir == '>') Console.WriteLine($"{e.Seconds,8:0.000}  > {e.Text}");
        else if (e.Dir == '<') Console.WriteLine($"{e.Seconds,8:0.000}  < {e.Text}");
        else if (e.Dir is '#' or 'i') Console.WriteLine($"{e.Seconds,8:0.000}  {(e.Dir == '#' ? "# MARKER" : "info")}: {e.Text}");
    };
    int listen = int.TryParse(a.Value("--listen"), out var lp) ? lp : 35000;
    FordDiag.Core.Simulation.FordSimulator? sim = null;
    Func<CancellationToken, ValueTask<FordDiag.Comms.Serial.ISerialLink>> open;
    if (a.Flag("--sim")) { sim = FordDiag.Core.Simulation.FordSimulator.Create(); open = _ => ValueTask.FromResult<FordDiag.Comms.Serial.ISerialLink>(sim.Host); }
    else
    {
        var port = a.Value("--port") ?? throw new ArgumentException("Specify --port <adapter port> (or --sim).");
        var adapter = FordAdapters.Find(a.Value("--adapter") ?? "vlinker-fs") ?? throw new ArgumentException("Unknown adapter. See 'fordiag adapters'.");
        var baud = int.TryParse(a.Value("--baud"), out var b) ? b : adapter.Profile.DefaultBaud;
        open = ct => FordDiag.Comms.Serial.SerialLinkFactory.OpenAsync(port, new FordDiag.Comms.Serial.SerialLinkOptions { BaudRate = baud, RtsCts = adapter.Profile.RtsCts }, ct);
    }
    await using var proxy = new TrafficProxy(open, log, listen, a.Flag("--remote"));
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await proxy.StartAsync(cts.Token);
    Console.Error.WriteLine($"Recording. In FORScan: Settings > Connection > WiFi, address 127.0.0.1, port {proxy.Port}.\nType a line and press Enter to add a marker before each action. Ctrl+C stops.");
    _ = Task.Run(() => { string? line; while (!cts.IsCancellationRequested && (line = Console.ReadLine()) is not null) if (line.Trim().Length > 0) log.Mark(line.Trim()); });
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
    var outPath = a.Value("--out") ?? $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl";
    log.Save(outPath);
    Console.Error.WriteLine($"\nSaved {log.Count} lines to {outPath}");
    PrintDecoded(ElmTrafficDecoder.Decode(log.Snapshot()));
    if (sim is not null) await sim.DisposeAsync();
    return 0;
}

static IReadOnlyList<CaptureEvent> LoadCapture(string? path)
{
    if (path is null) throw new ArgumentException("Capture file required.");
    return path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? CaptureLog.Load(path) : CaptureLog.FromText(File.ReadAllText(path));
}

static void PrintDecoded(DecodedCapture d)
{
    int seg = -1;
    foreach (var x in d.Exchanges.Where(e => e.Service != 0x3E))
    {
        if (x.Segment != seg) { seg = x.Segment; Console.WriteLine(seg == 0 ? "\n== before the first marker ==" : $"\n== {d.Markers[seg - 1].Label} =="); }
        Console.WriteLine($"{x.Seconds,8:0.0}s  {x.TxId:X3}  {UdsAnnotator.Describe(x)}");
        Console.WriteLine($"{"",16}{x.RequestHex}  ->  {x.ResponseHex}{(x.PendingCount > 0 ? $"  (+{x.PendingCount} pending)" : "")}");
    }
    var s = CaptureAnalysis.Summarize(d);
    Console.WriteLine($"\n{s.Exchanges} requests, {s.Modules.Count} module(s){(s.Vin is null ? "" : ", VIN " + s.Vin)}.");
    if (s.SecurityAccess) Console.WriteLine("Security access (0x27) was used: keys cannot be replayed.");
    if (s.Programming) Console.WriteLine("Programming requests were seen. Do not replay them.");
}

static int WriteProcedures(DecodedCapture d, string dir)
{
    int n = 0;
    var vin = CaptureAnalysis.FindVin(d);
    for (int i = 0; i <= d.Markers.Count; i++)
    {
        var seg = CaptureAnalysis.Segment(d, i);
        string name = i == 0 ? "Captured procedure" : d.Markers[i - 1].Label;
        try
        {
            bool test = seg.Any(e => e.Service == 0x2F);
            var p = CaptureAnalysis.BuildProcedure(seg, name, vin, test ? FordDiag.Core.Service.ProcedureKind.OutputTest : FordDiag.Core.Service.ProcedureKind.Service);
            var path = Path.Combine(dir, p.Id + ".json");
            File.WriteAllText(path, CaptureAnalysis.ToJson(new[] { p }));
            Console.WriteLine($"{path}: {p.Steps.Count} step(s), {p.Cleanup.Count} cleanup");
            n++;
        }
        catch (InvalidOperationException) { }
    }
    if (n == 0) Console.Error.WriteLine("No marker section contains service, routine or output-control requests. Add markers with `fordiag capture` before each action.");
    return n > 0 ? 0 : 1;
}

static ModuleSession Session(Args a, IAddressableTransport t)
{
    var m = FordModules.Find(a.Positional(1) ?? throw new ArgumentException("Module required.")) ?? throw new ArgumentException("Unknown module.");
    var bus = a.Value("--bus")?.ToLowerInvariant() switch { "hs" => FordBus.HsCan, "ms" => FordBus.MsCan, null => m.TypicalBus, _ => throw new ArgumentException("--bus must be hs or ms.") };
    return new ModuleSession(t, m, bus);
}

static ushort ParseDid(string? s) =>
    s is not null && ushort.TryParse(s.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out var d)
        ? d : throw new ArgumentException("DID must be 4 hex digits, e.g. F190.");

static int AsBuilt(Args a)
{
    AsBuiltData Load(string? path)
    {
        var d = AsBuiltData.Parse(File.ReadAllText(path ?? throw new ArgumentException("File required.")), out var errs);
        foreach (var e in errs) Console.Error.WriteLine($"{path}: {e}");
        return d;
    }
    switch (a.Positional(1))
    {
        case "check":
            var d = Load(a.Positional(2));
            Console.WriteLine($"{d.Lines.Count} line(s), {d.Modules.Count} module(s).");
            var bad = d.VerifyChecksums();
            foreach (var (k, exp, act) in bad) Console.WriteLine($"{k}: checksum {act:X2}, expected {exp:X2}");
            return bad.Count == 0 ? 0 : 1;
        case "fix":
            var f = Load(a.Positional(2));
            int n = f.FixChecksums();
            if (a.Positional(3) is { } outPath) File.WriteAllText(outPath, f.Format()); else Console.Write(f.Format());
            Console.Error.WriteLine($"{n} checksum(s) recalculated.");
            return 0;
        case "decode":
        {
            var data = Load(a.Positional(2));
            var m = FordModules.Find(a.Positional(3) ?? "") ?? throw new ArgumentException("Module required, e.g. 7D0.");
            var img = FordDiag.Core.Coding.AsBuiltImage.FromLines(data, m.RequestId, out var warns);
            foreach (var w in warns) Console.Error.WriteLine(w);
            var def = FordDiag.Core.Coding.DefinitionLibrary.Find(FordDiag.Core.Coding.DefinitionLibrary.Load(), m.RequestId, img.BlockLengths);
            if (def is null) { Console.WriteLine($"No option definitions match {m.Abbrev} with these block sizes. Raw blocks:"); Console.Write(img.ToText()); return 1; }
            Console.WriteLine($"{def.Name} ({def.License})");
            foreach (var (block, bytes) in img.Blocks)
                foreach (var fld in def.Block(block)?.Fields.Where(x => x.Fits(bytes.Length)) ?? Enumerable.Empty<FordDiag.Core.Coding.CodingField>())
                    Console.WriteLine($"  [{block}] {fld.Name,-52} {fld.Describe(fld.ReadRaw(bytes))}");
            return 0;
        }
        case "diff":
            var diffs = AsBuiltData.Diff(Load(a.Positional(2)), Load(a.Positional(3)));
            foreach (var x in diffs)
                Console.WriteLine($"{x.Key}  {(x.Before is null ? "(absent)" : Hex.ToString(x.Before, true))} -> {(x.After is null ? "(absent)" : Hex.ToString(x.After, true))}");
            Console.WriteLine($"{diffs.Count} difference(s).");
            return diffs.Count == 0 ? 0 : 1;
        default: Console.Error.WriteLine(Usage); return 2;
    }
}

static async Task<Connection> Connect(Args a)
{
    var trace = a.Flag("--trace") ? (Action<string>)(s => Console.Error.WriteLine(s)) : null;
    if (a.Flag("--sim"))
    {
        var sim = FordSimulator.Create(stn: false);
        var elm = await ElmTransport.ConnectAsync(sim.Host, new ElmOptions { Profile = ElmProfiles.Elm327Clone, Trace = trace });
        Console.Error.WriteLine("[simulator] virtual Ford: HS-CAN PCM/TCM/ABS, MS-CAN BCM/IPC");
        return new Connection(elm, BusSwitching.Automatic, sim);
    }
    var port = a.Value("--port") ?? throw new ArgumentException("Specify --port <name> (see 'fordiag ports') or --sim.");
    var adapter = FordAdapters.Find(a.Value("--adapter") ?? "vlinker-fs") ?? throw new ArgumentException("Unknown adapter. See 'fordiag adapters'.");
    var opts = new ElmOptions { Profile = adapter.Profile, Trace = trace };
    if (int.TryParse(a.Value("--baud"), out var baud)) opts.InitialBaud = baud;
    var e = await ElmTransport.OpenAsync(port, opts);
    Console.Error.WriteLine($"Connected: {e.Info.Version} ({adapter.DisplayName})");
    return new Connection(e, adapter.Switching, null);
}

sealed class Connection : IAsyncDisposable
{
    private readonly ElmTransport _elm;
    private readonly FordSimulator? _sim;
    public Connection(ElmTransport elm, BusSwitching sw, FordSimulator? sim) { _elm = elm; Switching = sw; _sim = sim; }
    public IAddressableTransport Transport => _elm;
    public BusSwitching Switching { get; }
    public async ValueTask<double?> ReadVoltage(CancellationToken ct) => await _elm.ReadVoltageAsync(ct);
    public async ValueTask DisposeAsync() { await _elm.DisposeAsync(); if (_sim is not null) await _sim.DisposeAsync(); }
}

sealed class Args
{
    private static readonly HashSet<string> Flags = new() { "--sim", "--full", "--clear", "--yes", "--trace", "--remote" };
    private readonly List<string> _pos = new();
    private readonly Dictionary<string, string> _vals = new();
    private readonly HashSet<string> _flags = new();

    public Args(string[] argv)
    {
        for (int i = 0; i < argv.Length; i++)
        {
            var s = argv[i];
            if (!s.StartsWith("--")) _pos.Add(s);
            else if (Flags.Contains(s)) _flags.Add(s);
            else if (i + 1 < argv.Length) _vals[s] = argv[++i];
            else throw new ArgumentException($"{s} needs a value.");
        }
    }
    public string? Positional(int i) => i < _pos.Count ? _pos[i] : null;
    public bool Flag(string f) => _flags.Contains(f);
    public string? Value(string k) => _vals.GetValueOrDefault(k);
}
