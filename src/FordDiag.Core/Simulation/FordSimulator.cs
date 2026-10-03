using System.Text;
using FordDiag.Comms.Serial;
using FordDiag.Comms.Simulation;
using FordDiag.Core.Live;

namespace FordDiag.Core.Simulation;

/// <summary>A simulated Ford: HS-CAN bus (500k: PCM, TCM, ABS) and MS-CAN bus (125k: BCM, IPC) behind one ELM327/STN adapter.</summary>
public sealed class FordSimulator : IAsyncDisposable
{
    public SimulatedBus Hs { get; }
    public SimulatedBus Ms { get; }
    public SimulatedElm Elm { get; private init; } = null!;
    public ISerialLink Host { get; private init; } = null!;
    public Dictionary<string, SimModule> Modules { get; } = new();

    public sealed class SimModule
    {
        public Dictionary<ushort, byte[]> Dids { get; } = new();
        public List<byte[]> Dtcs { get; } = new(); // 4 bytes each: dtc hi/mid/lo, status
        public EcuScript Script { get; init; } = null!;
        /// <summary>Mode 22 answers computed on every request (live-looking enhanced PIDs).</summary>
        public Dictionary<ushort, Func<byte[]>> Dynamic { get; } = new();
        /// <summary>Last 0x2F state per DID: null = control returned to the module.</summary>
        public Dictionary<ushort, byte[]?> Io { get; } = new();
        public List<string> Log { get; } = new();
        public uint RequestId { get; init; }
    }
    public Dictionary<uint, SimModule> ById { get; } = new();

    /// <summary>A synthetic F-150-style VIN (1FT = Ford truck, USA; 2020) with a valid check digit.</summary>
    public static readonly string Vin = MakeVin();
    private static string MakeVin()
    {
        var body = "1FTEW1EP0LFA12345";
        return body[..8] + FordDiag.Core.Vehicle.VinDecoder.CheckDigit(body) + body[9..];
    }

    public static FordSimulator Create(bool stn = false, bool msCanAvailable = true)
    {
        var hs = new SimulatedBus { SpeedKbps = 500 };
        var ms = new SimulatedBus { SpeedKbps = 125 };
        var opts = new SimulatedElmOptions
        {
            Version = stn ? "ELM327 v1.4b" : "ELM327 v1.5",
            StnId = stn ? "STN2120 v5.10.3" : null,
            BusSelector = p => p == 'B' ? ms : hs,
        };
        var (host, elm) = SimulatedElm.Create(hs, opts);
        var sim = new FordSimulator(hs, ms) { Elm = elm, Host = host };
        sim.AddModule("PCM", 0x7E0, hs, "AB3A-12A650-XA", "AB3A-14C204-XB", dtc: new byte[] { 0x01, 0x71, 0x00, 0x2F });
        sim.AddModule("TCM", 0x7E1, hs, "AB3P-7Z369-AA", "AB3P-14C204-AC");
        sim.AddModule("ABS", 0x760, hs, "AB31-2C405-BA", "AB31-14C204-BB", dtc: new byte[] { 0xC1, 0x00, 0x00, 0x09 });
        sim.AddLiveData(sim.Modules["PCM"]);
        if (msCanAvailable)
        {
            sim.AddModule("BCM", 0x726, ms, "BC3T-14B476-DA", "BC3T-14C204-DB", writable: Enumerable.Range(0xDE00, 6).Select(x => (ushort)x).ToArray());
            sim.AddModule("IPC", 0x720, ms, "AB5T-10849-CA", "AB5T-14C204-CB");
            sim.Modules["BCM"].Dids[0xDE00] = new byte[] { 0x01, 0x01, 0x01 };
            sim.Modules["BCM"].Dids[0xDE01] = new byte[] { 0x08, 0x40, 0x82, 0x00, 0x12 };
            sim.Modules["BCM"].Dids[0xDE02] = new byte[] { 0x00, 0x01, 0x00, 0x01 };
            sim.Modules["BCM"].Dids[0xDE03] = new byte[] { 0x00, 0x00, 0x00 };
            sim.Modules["BCM"].Dids[0xDE04] = new byte[] { 0x00, 0x00, 0x00, 0x00 };
            sim.Modules["BCM"].Dids[0xDE05] = new byte[] { 0x00, 0x00, 0x00, 0x00 };
            // SYNC 3 shaped configuration: block sizes 10,12,5,7,6,1,16,10,20 (DE00..DE08)
            var writable = Enumerable.Range(0xDE00, 9).Select(x => (ushort)x).ToArray();
            sim.AddModule("APIM", 0x7D0, ms, "JU5T-14G370-CC", "JU5T-14G371-CD", writable: writable);
            int[] sizes = { 10, 12, 5, 7, 6, 1, 16, 10, 20 };
            for (int i = 0; i < sizes.Length; i++) sim.Modules["APIM"].Dids[(ushort)(0xDE00 + i)] = new byte[sizes[i]];
            sim.Modules["APIM"].Dids[0xDE00] = new byte[] { 0x0A, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        }
        sim.AddServices();
        sim.AddEnhancedData("obdb-Ford-F-150");
        return sim;
    }

    private FordSimulator(SimulatedBus hs, SimulatedBus ms) { Hs = hs; Ms = ms; }

    private void AddModule(string name, uint tx, SimulatedBus bus, string strategy, string assembly, byte[]? dtc = null, ushort[]? writable = null)
    {
        var script = new EcuScript();
        var mod = new SimModule { Script = script, RequestId = tx };
        mod.Dids[0xF190] = Encoding.ASCII.GetBytes(Vin);
        mod.Dids[0xF188] = Encoding.ASCII.GetBytes(strategy);
        mod.Dids[0xF113] = Encoding.ASCII.GetBytes(assembly);
        if (dtc is not null) mod.Dtcs.Add(dtc);
        var canWrite = new HashSet<ushort>(writable ?? Array.Empty<ushort>());
        bool extended = false;

        script.On("22", req =>
        {
            if (req.Length != 3) return Neg(req, 0x13);
            ushort did = (ushort)(req[1] << 8 | req[2]);
            if (mod.Dynamic.TryGetValue(did, out var dyn)) return One(new byte[] { 0x62, req[1], req[2] }.Concat(dyn()).ToArray());
            return mod.Dids.TryGetValue(did, out var v)
                ? One(new byte[] { 0x62, req[1], req[2] }.Concat(v).ToArray())
                : Neg(req, 0x31);
        });
        script.On("2E", req =>
        {
            ushort did = (ushort)(req[1] << 8 | req[2]);
            if (!extended) return Neg(req, 0x7F);
            if (!canWrite.Contains(did)) return Neg(req, 0x33);
            mod.Dids[did] = req[3..];
            return One(new byte[] { 0x6E, req[1], req[2] });
        });
        script.On("10", req => { extended = req[1] == 0x03; return One(new byte[] { 0x50, req[1], 0x00, 0x32, 0x01, 0xF4 }); });
        script.On("3E", _ => One(new byte[] { 0x7E, 0x00 }));
        script.On("19", req => One(new byte[] { 0x59, 0x02, 0xFF }.Concat(mod.Dtcs.SelectMany(d => d)).ToArray()));
        script.On("14", _ => { mod.Dtcs.Clear(); return One(new byte[] { 0x54 }); });

        bus.Add(new FakeCanEcu(name, tx, tx + 8, script));
        Modules[name] = mod;
        ById[tx] = mod;
    }

    /// <summary>Service routines, resets, output control (0x2F) and a mode 08 support answer.</summary>
    private void AddServices()
    {
        foreach (var m in Modules.Values)
        {
            var mod = m;
            mod.Script.On("11", req => { mod.Log.Add("11 " + req[1].ToString("X2")); return One(new byte[] { 0x51, req[1] }); });
            mod.Script.On("2F", req =>
            {
                if (req.Length < 4) return Neg(req, 0x13);
                ushort did = (ushort)(req[1] << 8 | req[2]);
                if (did != 0xF001) return Neg(req, 0x31);
                mod.Io[did] = req[3] == 0x03 ? req[4..] : null;
                mod.Log.Add("2F " + Convert.ToHexString(req[1..]));
                return One(new byte[] { 0x6F, req[1], req[2], req[3] }.Concat(req[4..]).ToArray());
            });
        }
        if (Modules.ContainsKey("BCM")) Modules["BCM"].Script.On("31", req =>
        {
            if (req.Length >= 4 && req[1] == 0x01 && req[2] == 0x20 && req[3] == 0x1A) { Modules["BCM"].Log.Add("BMS reset"); return One(new byte[] { 0x71, 0x01, 0x20, 0x1A }); }
            return Neg(req, 0x31);
        });
        Modules["PCM"].Script.On("08", req => req[1] == 0 ? One(new byte[] { 0x48, 0x00, 0x80, 0x00, 0x00, 0x00 }) : Neg(req, 0x31));
    }

    /// <summary>Answers the mode 22 commands of an OBDb signal set with plausible, slowly changing values.</summary>
    private void AddEnhancedData(string setId)
    {
        var set = PidCatalog.Load().FirstOrDefault(x => x.Id == setId);
        if (set is null) return;
        foreach (var cmd in set.Commands)
        {
            if (!ById.TryGetValue(cmd.Header, out var mod) || mod.Dynamic.ContainsKey(cmd.Did) || mod.Dids.ContainsKey(cmd.Did)) continue;
            var c = cmd;
            mod.Dynamic[c.Did] = () =>
            {
                double t = Interlocked.Increment(ref _tick) / 8.0, sweep = (Math.Sin(t) + 1) / 2;
                var data = new byte[c.DataLength];
                foreach (var sg in c.Signals)
                {
                    if (sg.IsEnum) { sg.Encode(data, sg.Map!.Keys.Order().First()); continue; }
                    double lo = sg.Min ?? 0, hi = sg.Max is double m && m > lo ? m : lo + 100;
                    double v = sg.Unit switch
                    {
                        "noyes" or "offon" => Math.Floor(sweep * 1.99),
                        "celsius" => 70 + sweep * 30, "fahrenheit" => 160 + sweep * 50, "percent" => 20 + sweep * 60,
                        "kilopascal" => 100 + sweep * 100, "psi" => 30 + sweep * 10, "volts" => 12 + sweep * 2, "rpm" => 800 + sweep * 3000,
                        "kilometersPerHour" => sweep * 90, "degrees" => sweep * 40,
                        _ => lo + (hi - lo) * (0.25 + 0.5 * sweep),
                    };
                    sg.Encode(data, Math.Clamp(v, lo, Math.Max(lo, hi)));
                }
                return data;
            };
        }
    }

    private int _tick;

    /// <summary>Mode 01 answers with slowly changing values (idle -> revving) so live-data views have something to draw.</summary>
    private void AddLiveData(SimModule pcm)
    {
        pcm.Script.On("01", req =>
        {
            if (req.Length < 2) return Neg(req, 0x13);
            double t = Interlocked.Increment(ref _tick) / 8.0;
            double sweep = (Math.Sin(t) + 1) / 2; // 0..1
            byte pid = req[1];
            byte[]? data = pid switch
            {
                0x0C => U16((int)((850 + sweep * 2400) * 4)),
                0x0D => new[] { (byte)(sweep * 90) },
                0x05 => new[] { (byte)(40 + 50 + sweep * 4) },
                0x04 => new[] { (byte)(25 + sweep * 100) },
                0x11 => new[] { (byte)(20 + sweep * 70) },
                0x0B => new[] { (byte)(30 + sweep * 60) },
                0x0F => new byte[] { 40 + 28 },
                0x10 => U16((int)((2 + sweep * 30) * 100)),
                0x0E => new[] { (byte)(128 + 10 + sweep * 20) },
                0x06 => new[] { (byte)(128 + Math.Cos(t * 2) * 6) },
                0x07 => new byte[] { 130 },
                0x2F => new byte[] { 140 },
                0x42 => U16((int)(14100 + sweep * 200)),
                0x46 => new byte[] { 40 + 17 },
                0x5C => new[] { (byte)(40 + 88 + sweep * 3) },
                _ => null,
            };
            return data is null ? Neg(req, 0x12) : One(new byte[] { 0x41, pid }.Concat(data).ToArray());
        });
    }

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static IReadOnlyList<EcuReply> One(byte[] payload) => new[] { new EcuReply(payload) };
    private static IReadOnlyList<EcuReply> Neg(byte[] req, byte nrc) => One(new byte[] { 0x7F, req[0], nrc });

    public ValueTask DisposeAsync() => Elm.DisposeAsync();
}
