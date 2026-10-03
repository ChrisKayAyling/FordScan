using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Comms.Serial;
using FordDiag.Core;
using FordDiag.Core.Capture;
using FordDiag.Core.Live;
using FordDiag.Core.Service;
using FordDiag.Core.Simulation;

namespace FordDiag.Core.Tests;

public class DecoderTests
{
    private static IReadOnlyList<CaptureEvent> Log(string text) => CaptureLog.FromText(text);

    [Fact]
    public void ManualFramesWithFlowControlAreReassembled()
    {
        // what a tool in ATCAF0 mode exchanges: SF request, FF answer, tester flow control, consecutive frames
        var d = ElmTrafficDecoder.Decode(Log("""
            > ATZ
            < ELM327 v1.5
            > ATCAF0
            < OK
            > ATSH 7E0
            < OK
            > 0322F190
            < 10 14 62 F1 90 57 46 30
            > 3000000000000000
            < 21 58 58 58 47 43 44 4C
            < 22 31 32 33 34 35 36 37
            """));
        var x = Assert.Single(d.Exchanges);
        Assert.Equal(0x7E0u, x.TxId);
        Assert.Equal("22 F1 90", x.RequestHex);
        Assert.Equal(20, x.Response!.Length);
        Assert.Equal("WF0XXXGCDL1234567", System.Text.Encoding.ASCII.GetString(x.Response, 3, 17));
        Assert.Equal(2, d.ConfigCommands - 1);   // ATZ is counted too; two settings follow
    }

    [Fact]
    public void AutomaticFormattingPayloadsAndMultiLineAnswers()
    {
        var d = ElmTrafficDecoder.Decode(Log("""
            > ATSH 726
            < OK
            > 22F190
            < 014
            < 0: 62 F1 90 57 46 30
            < 1: 58 58 58 47 43 44 4C
            < 2: 31 32 33 34 35 36 37
            > 3E00
            < 7E 00
            """));
        Assert.Equal(2, d.Exchanges.Count);
        Assert.Equal(20, d.Exchanges[0].Response!.Length);
        Assert.Equal("22 F1 90", d.Exchanges[0].RequestHex);
        Assert.Equal("7E 00", d.Exchanges[1].ResponseHex);
        Assert.Equal(0x726u, d.Exchanges[1].TxId);
        Assert.Equal(0x72Eu, d.Exchanges[1].RxId);
    }

    [Fact]
    public void HeadersOnAndResponsePendingAreHandled()
    {
        var d = ElmTrafficDecoder.Decode(Log("""
            > ATH1
            < OK
            > ATCAF0
            < OK
            > ATSH 760
            < OK
            > 0431010203
            < 768 03 7F 31 78
            < 768 04 71 01 02 03
            > 0322F190
            < 768 03 7F 22 31
            """));
        Assert.Equal(2, d.Exchanges.Count);
        Assert.Equal(1, d.Exchanges[0].PendingCount);
        Assert.Equal("71 01 02 03", d.Exchanges[0].ResponseHex);
        Assert.True(d.Exchanges[1].Negative);
        Assert.Contains("DID 22", UdsAnnotator.Describe(d.Exchanges[1]).Replace("Read DID F190", "Read DID 22"));
    }

    [Fact]
    public void ErrorsAndEchoAreIgnoredOrRecorded()
    {
        var d = ElmTrafficDecoder.Decode(Log("""
            > ATE1
            < OK
            > 22F190
            < 22F190
            < NO DATA
            """));
        var x = Assert.Single(d.Exchanges);
        Assert.Null(x.Response);
        Assert.Equal("NO DATA", x.Error);
    }

    [Fact]
    public void MultiFrameRequestsAreAssembled()
    {
        var d = ElmTrafficDecoder.Decode(Log("""
            > ATCAF0
            < OK
            > ATSH 726
            < OK
            > 10082E DE 01 08 40
            < 30 00 00
            > 21 82 00 12
            < 03 6E DE 01
            """));
        var x = Assert.Single(d.Exchanges);
        Assert.Equal("2E DE 01 08 40 82 00 12", x.RequestHex);
        Assert.Equal("6E DE 01", x.ResponseHex);
        Assert.Contains("WRITE DID DE01", UdsAnnotator.Describe(x));
    }

    [Fact]
    public void MarkersSplitSegments()
    {
        var d = ElmTrafficDecoder.Decode(Log("""
            > ATCAF0
            < OK
            > 0322F190
            < 03 7F 22 31
            # start battery reset
            > 021003
            < 02 50 03
            # done
            > 021001
            < 02 50 01
            """));
        Assert.Equal(new[] { 0, 1, 2 }, d.Exchanges.Select(e => e.Segment));
        Assert.Equal(new[] { "start battery reset", "done" }, d.Markers.Select(m => m.Label));
    }

    [Theory]
    [InlineData("ATBRD 23", 114286)]
    [InlineData("atbrd10", 250000)]
    [InlineData("STSBR 921600", 921600)]
    [InlineData("STBR 115200", 115200)]
    [InlineData("ATZ", 0)]
    [InlineData("22F190", 0)]
    public void BaudChangeCommandsAreRecognised(string cmd, int expected) => Assert.Equal(expected, TrafficProxy.BaudChange(cmd));

    [Fact]
    public void LogFilesRoundTrip()
    {
        var log = new CaptureLog();
        log.Add('>', "ATZ"); log.Add('<', "ELM327 v1.5"); log.Add('P', ""); log.Mark("hello");
        var path = Path.Combine(Path.GetTempPath(), "cap-" + Guid.NewGuid().ToString("N") + ".jsonl");
        log.Save(path);
        var back = CaptureLog.Load(path);
        Assert.Equal(log.Snapshot().Select(e => (e.Dir, e.Text)), back.Select(e => (e.Dir, e.Text)));
    }
}

public class ProxyEndToEndTests : IAsyncLifetime
{
    private FordSimulator _sim = null!;
    private CaptureLog _log = null!;
    private TrafficProxy _proxy = null!;

    public async Task InitializeAsync()
    {
        _sim = FordSimulator.Create();
        _log = new CaptureLog();
        _proxy = new TrafficProxy(_ => ValueTask.FromResult<ISerialLink>(_sim.Host), _log, port: 0);
        await _proxy.StartAsync();
    }

    public async Task DisposeAsync() { await _proxy.DisposeAsync(); await _sim.DisposeAsync(); }

    private async Task<ElmTransport> ClientAsync(IsoTpMode mode)
    {
        var link = await TcpSerialLink.ConnectAsync("127.0.0.1", _proxy.Port, TimeSpan.FromSeconds(5));
        return await ElmTransport.ConnectAsync(link, new ElmOptions { Profile = ElmProfiles.Elm327Clone, IsoTp = mode });
    }

    [Theory]
    [InlineData(IsoTpMode.Manual)]
    [InlineData(IsoTpMode.ElmAutomatic)]
    public async Task ARealRunThroughTheProxyIsDecodedAndTurnedIntoAProcedure(IsoTpMode mode)
    {
        await using (var client = await ClientAsync(mode))
        {
            var pcm = new ModuleSession(client, FordModules.Find("PCM")!, FordBus.HsCan);
            await pcm.ReadDidAsync(0xF190);
            var bcm = new ModuleSession(client, FordModules.Find("BCM")!, FordBus.MsCan);
            _log.Mark("battery reset");
            var bms = ProcedureLibrary.Load().Single(p => p.Id == "bms-reset");
            var r = await ProcedureRunner.RunAsync(bcm, bms, new ProcedureOptions { Commit = true });
            Assert.True(r.Success, r.Message);
            _log.Mark("end");
            await bcm.ReadDidAsync(0xDE01);
        }
        await Task.Delay(100);

        var d = ElmTrafficDecoder.Decode(_log.Snapshot());
        var s = CaptureAnalysis.Summarize(d);
        Assert.Equal(FordSimulator.Vin, s.Vin);
        Assert.Contains(d.Exchanges, e => e.RequestHex == "31 01 20 1A" && e.ResponseHex == "71 01 20 1A" && e.TxId == 0x726);
        Assert.False(s.SecurityAccess);
        Assert.True(s.Writes >= 1);

        var seg = CaptureAnalysis.Segment(d, 1);
        var draft = CaptureAnalysis.BuildProcedure(seg, "Battery reset (captured)", s.Vin);
        Assert.Equal(0x726u, draft.Module);
        Assert.Equal(new[] { "10 03", "31 01 20 1A" }, draft.Steps.Select(x => x.Hex));
        Assert.Equal("71 01 20 1A", draft.Steps[1].Expect);
        Assert.Equal(new[] { "10 01" }, draft.Cleanup.Select(x => x.Hex));
        Assert.Equal("captured", draft.Confidence);

        // the draft survives a trip through its own JSON and is accepted by the loader
        var back = ProcedureLibrary.ParseJson(CaptureAnalysis.ToJson(new[] { draft })).Single();
        Assert.Equal(draft.Steps.Select(x => x.Hex), back.Steps.Select(x => x.Hex));
        Assert.Equal(draft.Module, back.Module);

        // as-built block read at the end is recoverable, and DID inventory has lengths
        var img = CaptureAnalysis.AsBuiltFrom(d, 0x726);
        Assert.Equal(new byte[] { 0x08, 0x40, 0x82, 0x00, 0x12 }, img.Blocks[2]);
        Assert.Contains(CaptureAnalysis.DidInventory(d), x => x.Module == 0x7E0 && x.Did == 0xF190 && x.Length == 17);
    }

    [Fact]
    public async Task OutputTestCapturesKeepTheHandBackAsCleanup()
    {
        await using (var client = await ClientAsync(IsoTpMode.Manual))
        {
            var pcm = new ModuleSession(client, FordModules.Find("PCM")!, FordBus.HsCan);
            var fan = ProcedureLibrary.ParseJson("""
                { "procedures": [ { "id": "fan", "name": "fan", "kind": "outputTest", "module": "7E0",
                  "steps": [ { "hex": "10 03", "desc": "s" }, { "hex": "2F F0 01 03 64", "expect": "6F F0 01 03", "desc": "on", "hold": 2 } ],
                  "cleanup": [ { "hex": "2F F0 01 00", "desc": "off" }, { "hex": "10 01", "desc": "s" } ] } ] }
                """)[0];
            _log.Mark("fan");
            await ProcedureRunner.RunAsync(pcm, fan, new ProcedureOptions { Commit = true, KeepAliveInterval = TimeSpan.FromMilliseconds(300) });
        }
        await Task.Delay(100);
        var d = ElmTrafficDecoder.Decode(_log.Snapshot());
        var draft = CaptureAnalysis.BuildProcedure(CaptureAnalysis.Segment(d, 1), "Fan (captured)", kind: ProcedureKind.OutputTest);
        Assert.Equal(new[] { "10 03", "2F F0 01 03 64" }, draft.Steps.Select(x => x.Hex));
        Assert.Equal(new[] { "2F F0 01 00", "10 01" }, draft.Cleanup.Select(x => x.Hex));
        Assert.True(draft.Steps[1].HoldSeconds >= 1);          // the 2 s hold shows up as a gap (tester present is ignored)
    }

    [Fact]
    public async Task DidInventoryBecomesAPidSetSkeleton()
    {
        await using (var client = await ClientAsync(IsoTpMode.Manual))
        {
            var pcm = new ModuleSession(client, FordModules.Find("PCM")!, FordBus.HsCan);
            var set = PidCatalog.Load().Single(s => s.Id == "obdb-Ford-F-150");
            foreach (var c in set.Commands.Where(c => c.Header == 0x7E0).Take(3)) await PidReader.ReadAsync(pcm, c);
        }
        await Task.Delay(100);
        var d = ElmTrafficDecoder.Decode(_log.Snapshot());
        var json = CaptureAnalysis.PidSetDraft(d, "test");
        var parsed = PidCatalog.FromJson(json);
        Assert.Equal(3, parsed.Commands.Count);
        Assert.All(parsed.Commands, c => Assert.Equal(0x7E0u, c.Header));
    }

    [Fact]
    public async Task ProxyReportsClientState()
    {
        Assert.False(_proxy.HasClient);
        await using var client = await ClientAsync(IsoTpMode.Manual);
        for (int i = 0; i < 50 && !_proxy.HasClient; i++) await Task.Delay(20);
        Assert.True(_proxy.HasClient);
        Assert.True(_proxy.BytesToAdapter > 0);
    }
}
