using System.Net;
using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Core;
using FordDiag.Core.Live;
using FordDiag.Core.Service;
using FordDiag.Core.Simulation;
using FordDiag.Core.Vehicle;

namespace FordDiag.Core.Tests;

public class VinTests
{
    [Fact]
    public void ValidNorthAmericanFordVin()
    {
        // 1FTEW1EP5KFA12345 style example with a computed check digit
        string body = "1FTEW1EP?KFA12345";
        char cd = VinDecoder.CheckDigit(body.Replace('?', '0'));
        var vin = body.Replace('?', cd);
        var i = VinDecoder.Decode(vin);
        Assert.True(i.WellFormed); Assert.True(i.CheckDigitApplies); Assert.True(i.CheckDigitOk);
        Assert.Equal("1FT", i.Wmi); Assert.Equal("United States", i.Country); Assert.True(i.IsFord);
        Assert.Equal(new[] { 2019 }, i.ModelYears);            // K = 2019, position 7 is a letter (P) -> 2010-2039
        Assert.Equal('F', i.PlantCode);
    }

    [Fact]
    public void KnownVinCheckDigitExample()
    {
        // The widely used ISO 3779 worked example: 1M8GDM9AXKP042788 (check digit X)
        Assert.Equal('X', VinDecoder.CheckDigit("1M8GDM9AXKP042788"));
        Assert.True(VinDecoder.Decode("1M8GDM9AXKP042788").CheckDigitOk);
        Assert.False(VinDecoder.Decode("1M8GDM9A1KP042788").CheckDigitOk);
    }

    [Fact]
    public void SimulatorVinIsAValidUsFordVin()
    {
        var i = VinDecoder.Decode(FordSimulator.Vin);
        Assert.True(i.CheckDigitOk); Assert.Equal("United States", i.Country); Assert.Equal(2020, i.ModelYear);
    }

    [Fact]
    public void EuropeanFordVinHasNoCheckDigitRuleAndTwoYears()
    {
        var i = VinDecoder.Decode("WF0XXXGCDL1234567");        // Ford of Europe
        Assert.False(i.CheckDigitApplies);
        Assert.Equal("Germany", i.Country);
        Assert.Equal(2, i.ModelYears.Count);
        Assert.Equal(i.ModelYears[0] - 30, i.ModelYears[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1FT")]
    [InlineData("1FTEW1EP5KFA1234I")]   // contains I
    public void BadVinsAreFlagged(string vin) => Assert.False(VinDecoder.Decode(vin).WellFormed);

    [Fact]
    public void NhtsaResponseIsParsed()
    {
        const string json = """
            {"Count":1,"Results":[{"Make":"FORD","Model":"F-150","ModelYear":"2019","Trim":"XLT","Series":"","BodyClass":"Pickup","DriveType":"4WD/4-Wheel Drive/4x4",
             "DisplacementL":"3.5","EngineCylinders":"6","FuelTypePrimary":"Gasoline","EngineModel":"EcoBoost","PlantCity":"DEARBORN","ErrorCode":"0","ErrorText":"0 - VIN decoded clean"}]}
            """;
        var d = NhtsaVinClient.Parse(json);
        Assert.Equal("2019 FORD F-150 XLT", d.Title);
        Assert.Equal("3.5L 6-cyl Gasoline EcoBoost", d.Engine);
        Assert.Null(d.Error);
        Assert.Equal("Bad VIN", NhtsaVinClient.Parse("""{"Results":[{"ErrorCode":"1","ErrorText":"Bad VIN"}]}""").Error);
    }

    private sealed class Stub(string body) : HttpMessageHandler
    {
        public string? Url;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Url = request.RequestUri!.ToString(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }); }
    }

    [Fact]
    public async Task LookupCallsTheVpicEndpoint()
    {
        var stub = new Stub("""{"Results":[{"Make":"FORD","Model":"Focus","ModelYear":"2016","ErrorCode":"0"}]}""");
        var d = await new NhtsaVinClient(new HttpClient(stub)).LookupAsync("1FADP3F20GL123456");
        Assert.Contains("DecodeVinValues/1FADP3F20GL123456?format=json", stub.Url);
        Assert.Equal("Focus", d.Model);
    }
}

public class PidTests
{
    private static PidSignal Sig(int bix, int len, double mul = 1, double div = 1, double add = 0, bool signed = false, double? min = null, double? max = null) =>
        new() { Id = "x", Name = "x", Bix = bix, Len = len, Mul = mul, Div = div, Add = add, Signed = signed, Min = min, Max = max };

    [Fact]
    public void DecodesPerObdbRules()
    {
        // 16-bit tire pressure: (256A+B)/20 psi, as in the F-150 definition
        Assert.Equal(35.0, Sig(0, 16, div: 20, max: 3276).Decode(new byte[] { 0x02, 0xBC }));
        // 8-bit temperature with offset
        Assert.Equal(50, Sig(0, 8, add: -40, min: -40, max: 215).Decode(new byte[] { 90 }));
        // bit field inside a byte (MSB first): bits 2..4
        Assert.Equal(5, Sig(2, 3, max: 7).Decode(new byte[] { 0b0010_1000 }));
        // signed 16-bit
        Assert.Equal(-2, Sig(0, 16, signed: true, min: -100, max: 100).Decode(new byte[] { 0xFF, 0xFE }));
        // clamped to max
        Assert.Equal(100, Sig(0, 8, max: 100).Decode(new byte[] { 250 }));
    }

    [Fact]
    public void NullBoundsMeanNoValueAndByteSwap()
    {
        var s = new PidSignal { Id = "x", Name = "x", Len = 16, Max = 65535, NullMax = 65535 };
        Assert.Null(s.Decode(new byte[] { 0xFF, 0xFF }));
        var le = new PidSignal { Id = "x", Name = "x", Len = 16, Max = 65535, ByteSwapped = true };
        Assert.Equal(0x0201, le.Decode(new byte[] { 0x01, 0x02 }));
    }

    [Fact]
    public void EncodeThenDecodeRoundTrips()
    {
        foreach (var s in new[] { Sig(0, 16, div: 20, max: 3276), Sig(4, 12, max: 4095), Sig(0, 8, add: -40, min: -40, max: 215), Sig(0, 16, signed: true, min: -300, max: 300) })
        {
            var data = new byte[4];
            double v = s.Signed ? -123 : 77;
            s.Encode(data, v);
            Assert.Equal(v, s.Decode(data)!.Value, 1);
        }
    }

    [Fact]
    public void CatalogLoadsOdbdSets()
    {
        var all = PidCatalog.Load();
        Assert.True(all.Count >= 25);
        Assert.True(all.Sum(s => s.SignalCount) > 4500);
        Assert.All(all, s => { Assert.Equal("CC-BY-SA-4.0", s.License); Assert.Contains("OBDb", s.Attribution); });
        var f150 = all.Single(s => s.Id == "obdb-Ford-F-150");
        Assert.Contains(f150.Commands, c => c.Header == 0x726 && c.Did == 0x2815);   // tire pressure from the BCM
        var tire = f150.Commands.Single(c => c.Header == 0x726 && c.Did == 0x2815).Signals.Single();
        Assert.Equal("psi", tire.Unit);
        Assert.Equal(16, tire.Len);
    }

    [Fact]
    public void SetsAreChosenByModelAndYear()
    {
        var all = PidCatalog.Load();
        Assert.Equal("obdb-Ford-F-150", PidCatalog.SetsFor(all, "F-150", 2019)[0].Id);
        Assert.Equal("obdb-Ford-Mustang-Mach-E", PidCatalog.SetsFor(all, "Mustang Mach-E", 2022)[0].Id);
        Assert.Equal("obdb-Ford-Mustang", PidCatalog.SetsFor(all, "Mustang", 2018)[0].Id);
        var unknown = PidCatalog.SetsFor(all, "Nonexistent", 2020);
        Assert.Equal("obdb-Ford", unknown[0].Id);                                     // generic fallback
        Assert.Equal("obdb-Ford-Ranger-2005-2018", PidCatalog.SetsFor(all, "Ranger", 2010)[0].Id);   // year specific file first
        Assert.DoesNotContain(PidCatalog.SetsFor(all, "Ranger", 2022), s => s.Id.Contains("2005-2018"));
    }

    [Fact]
    public async Task EnhancedPidsAreReadAndDecodedFromTheSimulator()
    {
        await using var sim = FordSimulator.Create();
        await using var elm = await ElmTransport.ConnectAsync(sim.Host, new ElmOptions { Profile = ElmProfiles.Elm327Clone });
        var set = PidCatalog.Load().Single(s => s.Id == "obdb-Ford-F-150");
        var cmd = set.Commands.First(c => c.Header == 0x7E0);
        var pcm = new ModuleSession(elm, FordModules.Find("PCM")!, FordBus.HsCan);
        var data = await PidReader.ReadAsync(pcm, cmd);
        Assert.Equal(cmd.DataLength, data.Length);
        Assert.All(cmd.Signals, s => Assert.NotNull(s.Format(data)));
        // modules the simulator does not have answer with an NRC
        var missing = set.Commands.First(c => !sim.ById.ContainsKey(c.Header));
        await Assert.ThrowsAsync<EcuTimeoutException>(async () => await PidReader.ReadAsync(new ModuleSession(elm, FordModules.Unknown(missing.Header), FordBus.HsCan), missing));
    }
}

public class ProcedureTests : IAsyncLifetime
{
    private FordSimulator _sim = null!;
    private ElmTransport _elm = null!;
    public async Task InitializeAsync()
    {
        _sim = FordSimulator.Create();
        _elm = await ElmTransport.ConnectAsync(_sim.Host, new ElmOptions { Profile = ElmProfiles.Elm327Clone });
    }
    public async Task DisposeAsync() { await _elm.DisposeAsync(); await _sim.DisposeAsync(); }

    private static readonly IReadOnlyList<Procedure> Lib = ProcedureLibrary.Load();
    private ModuleSession On(string abbrev, FordBus bus) => new(_elm, FordModules.Find(abbrev)!, bus);

    [Fact]
    public void BundledProceduresLoad()
    {
        Assert.Contains(Lib, p => p.Id == "bms-reset" && p.Module == 0x726 && p.Steps.Count == 2 && p.Confidence == "reported");
        Assert.Contains(Lib, p => p.Id == "reset-module-hard" && p.Module is null && p.Confidence == "standard");
        Assert.Contains(Lib, p => p.Kind == ProcedureKind.OutputTest);
        Assert.All(Lib, p => Assert.All(p.Steps, s => Assert.NotEmpty(s.Bytes)));
    }

    [Fact]
    public async Task DryRunSendsNothing()
    {
        var bms = Lib.Single(p => p.Id == "bms-reset");
        var r = await ProcedureRunner.RunAsync(On("BCM", FordBus.MsCan), bms, new ProcedureOptions { Commit = false });
        Assert.True(r.Success);
        Assert.Equal(2, r.Steps.Count);
        Assert.Empty(_sim.Modules["BCM"].Log);
    }

    [Fact]
    public async Task BmsResetRunsStepsAndReturnsToDefaultSession()
    {
        var bms = Lib.Single(p => p.Id == "bms-reset");
        var r = await ProcedureRunner.RunAsync(On("BCM", FordBus.MsCan), bms, new ProcedureOptions { Commit = true });
        Assert.True(r.Success, r.Message);
        Assert.Contains("BMS reset", _sim.Modules["BCM"].Log);
        Assert.Equal(new[] { "10 03", "31 01 20 1A", "10 01" }, r.Steps.Select(s => s.Hex));
        Assert.All(r.Steps, s => Assert.True(s.Ok));
    }

    [Fact]
    public async Task FailedStepStopsAndStillRunsCleanup()
    {
        var bad = ProcedureLibrary.ParseJson("""
            { "procedures": [ { "id": "t", "name": "t", "steps": [ { "hex": "10 03", "desc": "session" }, { "hex": "31 01 FF FF", "desc": "unknown routine" }, { "hex": "10 03", "desc": "never" } ],
              "cleanup": [ { "hex": "10 01", "desc": "back" } ] } ] }
            """)[0];
        var r = await ProcedureRunner.RunAsync(On("BCM", FordBus.MsCan), bad, new ProcedureOptions { Commit = true });
        Assert.False(r.Success);
        Assert.Contains("NRC 0x31", r.Message);
        Assert.Equal(new[] { "10 03", "31 01 FF FF", "10 01" }, r.Steps.Select(s => s.Hex));
    }

    [Fact]
    public async Task LowVoltageRefusesBeforeSending()
    {
        var bms = Lib.Single(p => p.Id == "bms-reset");
        var r = await ProcedureRunner.RunAsync(On("BCM", FordBus.MsCan), bms, new ProcedureOptions { Commit = true, ReadVoltage = _ => ValueTask.FromResult<double?>(11.4) });
        Assert.False(r.Success);
        Assert.Contains("11.4", r.Message);
        Assert.Empty(r.Steps);
        Assert.Empty(_sim.Modules["BCM"].Log);
    }

    [Fact]
    public async Task ModuleResetWorksOnAnyModule()
    {
        var hard = Lib.Single(p => p.Id == "reset-module-hard");
        var r = await ProcedureRunner.RunAsync(On("ABS", FordBus.HsCan), hard, new ProcedureOptions { Commit = true });
        Assert.True(r.Success);
        Assert.Contains("11 01", _sim.Modules["ABS"].Log);
    }

    private const string FanTest = """
        { "procedures": [ { "id": "fan", "name": "Cooling fan", "kind": "outputTest", "module": "7E0", "minVoltage": 11.5,
            "steps": [ { "hex": "10 03", "desc": "session" }, { "hex": "2F F0 01 03 64", "expect": "6F F0 01 03", "desc": "Fan 100 %", "hold": 0.3 } ],
            "cleanup": [ { "hex": "2F F0 01 00", "expect": "6F F0 01 00", "desc": "Return control to the module" }, { "hex": "10 01", "desc": "default session" } ] } ] }
        """;

    [Fact]
    public async Task OutputTestHoldsThenReturnsControl()
    {
        var fan = ProcedureLibrary.ParseJson(FanTest)[0];
        Assert.Equal(ProcedureKind.OutputTest, fan.Kind);
        var pcm = On("PCM", FordBus.HsCan);
        var task = ProcedureRunner.RunAsync(pcm, fan, new ProcedureOptions { Commit = true, KeepAliveInterval = TimeSpan.FromMilliseconds(100) });
        var r = await task;
        Assert.True(r.Success, r.Message);
        Assert.Null(_sim.Modules["PCM"].Io[0xF001]);            // control returned
        Assert.Contains(_sim.Modules["PCM"].Log, l => l.StartsWith("2F F00103"));
        Assert.Equal("2F F0 01 00", r.Steps[^2].Hex);
    }

    [Fact]
    public async Task StoppingAnOutputTestStillReturnsControl()
    {
        var fan = ProcedureLibrary.ParseJson(FanTest.Replace("\"hold\": 0.3", "\"hold\": 30"))[0];
        using var cts = new CancellationTokenSource();
        var run = ProcedureRunner.RunAsync(On("PCM", FordBus.HsCan), fan, new ProcedureOptions { Commit = true, KeepAliveInterval = TimeSpan.FromMilliseconds(100) }, cts.Token);
        for (int i = 0; i < 100 && !_sim.Modules["PCM"].Io.ContainsKey(0xF001); i++) await Task.Delay(20);
        Assert.NotNull(_sim.Modules["PCM"].Io[0xF001]);          // fan commanded on
        cts.Cancel();
        var r = await run;
        Assert.False(r.Success); Assert.Equal("Stopped.", r.Message);
        Assert.Null(_sim.Modules["PCM"].Io[0xF001]);            // ... and handed back
    }

    [Fact]
    public async Task Mode08SupportProbe()
    {
        var p = Lib.Single(x => x.Id == "obd2-mode08-support");
        var r = await ProcedureRunner.RunAsync(On("PCM", FordBus.HsCan), p, new ProcedureOptions { Commit = true });
        Assert.True(r.Success, r.Message);
        Assert.StartsWith("48 00", r.Steps[0].Response);
    }

    [Fact]
    public void BadProcedureFilesAreReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fd-proc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ok.json"), FanTest);
        File.WriteAllText(Path.Combine(dir, "bad.json"), """{ "procedures": [ { "id": "x", "name": "x", "steps": [ { "hex": "ZZ", "desc": "" } ] } ] }""");
        var warnings = new List<string>();
        var lib = ProcedureLibrary.Load(dir, warnings.Add);
        Assert.Contains(lib, p => p.Id == "fan");
        Assert.Single(warnings);
    }
}
