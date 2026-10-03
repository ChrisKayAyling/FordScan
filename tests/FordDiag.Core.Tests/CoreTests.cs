using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.Core;
using FordDiag.Core.Simulation;

namespace FordDiag.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void ResponseIdIsRequestPlus8()
    {
        var bcm = FordModules.Find("bcm")!;
        Assert.Equal(0x726u, bcm.RequestId);
        Assert.Equal(0x72Eu, bcm.ResponseId);
    }

    [Theory]
    [InlineData("7E0", "PCM")]
    [InlineData("0x760", "ABS")]
    [InlineData("7AB", "7AB")] // unknown but valid address
    public void FindByAddress(string text, string abbrev) => Assert.Equal(abbrev, FordModules.Find(text)!.Abbrev);

    [Theory]
    [InlineData("123")]
    [InlineData("zzz")]
    [InlineData("8000")]
    public void FindRejectsBadAddress(string text) => Assert.Null(FordModules.Find(text));

    [Fact]
    public void BusSpeeds()
    {
        Assert.Equal(CanSpeed.Kbps125, FordBus.MsCan.Speed());
        Assert.Equal(CanSpeed.Kbps500, FordBus.HsCan.Speed());
    }
}

public class AsBuiltTests
{
    [Fact]
    public void ParseFormatRoundTrip()
    {
        var d = AsBuiltData.Parse("726-01-01 0840 8200 12\n# comment\n726-02-01: 5E 00\n", out var errors);
        Assert.Empty(errors);
        Assert.Equal(2, d.Lines.Count);
        Assert.Equal("726-01-01 08 40 82 00 12\n726-02-01 5E 00\n".ReplaceLineEndings("\n"), d.Format().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ReportsBadLines()
    {
        AsBuiltData.Parse("726-01-01 0840\nXYZ-01-01 00\n726-01-02 0\n726-01-01 FF\n726-01-03\n", out var errors);
        Assert.Equal(4, errors.Count); // bad address, odd hex, duplicate, missing data
    }

    [Fact]
    public void DiffFindsChangedAddedRemoved()
    {
        var a = AsBuiltData.Parse("726-01-01 0840\n726-01-02 1111\n", out _);
        var b = AsBuiltData.Parse("726-01-01 0841\n726-01-03 2222\n", out _);
        var diff = AsBuiltData.Diff(a, b);
        Assert.Equal(3, diff.Count);
        Assert.Contains(diff, x => x.Key.ToString() == "726-01-01" && x.Before is not null && x.After is not null);
        Assert.Contains(diff, x => x.Key.ToString() == "726-01-02" && x.After is null);
        Assert.Contains(diff, x => x.Key.ToString() == "726-01-03" && x.Before is null);
    }
}

public class SimulatedVehicleTests : IAsyncLifetime
{
    private FordSimulator _sim = null!;
    private ElmTransport _elm = null!;

    public async Task InitializeAsync() => await OpenAsync(stn: false);

    private async Task OpenAsync(bool stn)
    {
        if (_elm is not null) await _elm.DisposeAsync();
        if (_sim is not null) await _sim.DisposeAsync();
        _sim = FordSimulator.Create(stn);
        _elm = await ElmTransport.ConnectAsync(_sim.Host, new ElmOptions { Profile = stn ? ElmProfiles.ObdLinkEx : ElmProfiles.Elm327Clone });
    }

    public async Task DisposeAsync() { await _elm.DisposeAsync(); await _sim.DisposeAsync(); }

    private static async Task<List<FoundModule>> ScanAsync(ElmTransport elm, ScanOptions o)
    {
        var list = new List<FoundModule>();
        await foreach (var m in FordScanner.ScanAsync(elm, o)) list.Add(m);
        return list;
    }

    [Theory]
    [InlineData(false, "ATSP B")]
    [InlineData(true, "STP 53")]
    public async Task ScanFindsModulesOnBothBuses(bool stn, string expectedMsCommand)
    {
        await OpenAsync(stn);
        var found = await ScanAsync(_elm, new ScanOptions());
        Assert.Equal(new[] { "ABS", "APIM", "BCM", "IPC", "PCM", "TCM" }.OrderBy(x => x), found.Select(f => f.Module.Abbrev).OrderBy(x => x));
        Assert.All(found.Where(f => f.Module.Abbrev is "PCM" or "TCM" or "ABS"), f => Assert.Equal(FordBus.HsCan, f.Bus));
        Assert.All(found.Where(f => f.Module.Abbrev is "BCM" or "IPC" or "APIM"), f => Assert.Equal(FordBus.MsCan, f.Bus));
        Assert.True(_sim.Elm.Commands.Any(c => c.Replace(" ", "") == expectedMsCommand.Replace(" ", "")), string.Join(" | ", _sim.Elm.Commands));
    }

    [Fact]
    public async Task ScanWithoutMsCanSupportSkipsMsBus()
    {
        var found = await ScanAsync(_elm, new ScanOptions { Switching = BusSwitching.None });
        Assert.DoesNotContain(found, f => f.Bus == FordBus.MsCan);
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public async Task ManualSwitchPromptsOncePerBus()
    {
        var asked = new List<FordBus>();
        await ScanAsync(_elm, new ScanOptions { Switching = BusSwitching.Manual, PromptBusSwitch = (b, _) => { asked.Add(b); return Task.CompletedTask; } });
        Assert.Equal(new[] { FordBus.HsCan, FordBus.MsCan }, asked);
    }

    [Fact]
    public async Task MsBusDownIsSkippedNotFatal()
    {
        await _sim.DisposeAsync(); await _elm.DisposeAsync();
        _sim = FordSimulator.Create(false, msCanAvailable: false);
        _elm = await ElmTransport.ConnectAsync(_sim.Host, new ElmOptions { Profile = ElmProfiles.Elm327Clone });
        var log = new List<string>();
        var found = await ScanAsync(_elm, new ScanOptions { Log = new Progress<string>(log.Add) });
        Assert.Equal(3, found.Count);
    }

    [Fact]
    public async Task ReadsIdentificationFromBcmOnMsCan()
    {
        var s = new ModuleSession(_elm, FordModules.Find("BCM")!, FordBus.MsCan);
        var ident = await s.ReadIdentificationAsync();
        Assert.Contains(ident, i => i.Did == 0xF190 && i.Value == FordSimulator.Vin);
        Assert.Contains(ident, i => i.Did == 0xF188 && i.Value == "BC3T-14B476-DA");
        Assert.DoesNotContain(ident, i => i.Did == 0xF18C); // unsupported DID skipped
    }

    [Fact]
    public async Task ReadsAndClearsDtcs()
    {
        var s = new ModuleSession(_elm, FordModules.Find("PCM")!, FordBus.HsCan);
        var dtcs = await s.ReadDtcsAsync();
        Assert.Single(dtcs);
        Assert.Equal("P0171-00", dtcs[0].Text);
        await s.ClearDtcsAsync();
        Assert.Empty(await s.ReadDtcsAsync());
    }

    private string TempDir() => Path.Combine(Path.GetTempPath(), "fordiag-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteDryRunChangesNothing()
    {
        var bcm = new ModuleSession(_elm, FordModules.Find("BCM")!, FordBus.MsCan);
        var r = await DidWriter.WriteAsync(_elm, bcm, 0xDE01, new byte[] { 1, 2, 3, 4, 5 }, new WriteOptions { Backups = new BackupStore(TempDir()) });
        Assert.False(r.Written);
        Assert.Equal(new byte[] { 0x08, 0x40, 0x82, 0x00, 0x12 }, _sim.Modules["BCM"].Dids[0xDE01]);
    }

    [Fact]
    public async Task WriteBacksUpWritesAndVerifies()
    {
        var dir = TempDir();
        var bcm = new ModuleSession(_elm, FordModules.Find("BCM")!, FordBus.MsCan);
        var r = await DidWriter.WriteAsync(_elm, bcm, 0xDE01, new byte[] { 1, 2, 3, 4, 5 }, new WriteOptions { Commit = true, Backups = new BackupStore(dir) });
        Assert.True(r.Written);
        Assert.Equal("Written and verified.", r.Message);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, _sim.Modules["BCM"].Dids[0xDE01]);
        var backup = BackupStore.Load(r.BackupPath!);
        Assert.Equal("08 40 82 00 12", backup.Hex);
        Assert.Equal(FordBus.MsCan, backup.Bus);
    }

    [Fact]
    public async Task WriteRefusedOnLowVoltageAndLengthMismatchAndNrc()
    {
        var bcm = new ModuleSession(_elm, FordModules.Find("BCM")!, FordBus.MsCan);
        var dir = new BackupStore(TempDir());
        await Assert.ThrowsAsync<WriteRefusedException>(() => DidWriter.WriteAsync(_elm, bcm, 0xDE01, new byte[5],
            new WriteOptions { Commit = true, Backups = dir, ReadVoltage = _ => ValueTask.FromResult<double?>(11.2) }));
        await Assert.ThrowsAsync<WriteRefusedException>(() => DidWriter.WriteAsync(_elm, bcm, 0xDE01, new byte[3],
            new WriteOptions { Commit = true, Backups = dir }));
        var ipc = new ModuleSession(_elm, FordModules.Find("IPC")!, FordBus.MsCan);
        _sim.Modules["IPC"].Dids[0xDE01] = new byte[2];
        var ex = await Assert.ThrowsAsync<WriteRefusedException>(() => DidWriter.WriteAsync(_elm, ipc, 0xDE01, new byte[2],
            new WriteOptions { Commit = true, Backups = dir }));
        Assert.Contains("0x33", ex.Message); // security access required
        Assert.Equal(new byte[] { 0x08, 0x40, 0x82, 0x00, 0x12 }, _sim.Modules["BCM"].Dids[0xDE01]);
    }
}

public class LiveDataTests
{
    [Fact]
    public void DecodesStandardFormulas()
    {
        Assert.Equal(850, ObdPids.TryDecode(ObdPids.Find(0x0C)!, new byte[] { 0x41, 0x0C, 0x0D, 0x48 }));
        Assert.Equal(50, ObdPids.TryDecode(ObdPids.Find(0x05)!, new byte[] { 0x41, 0x05, 90 }));
        Assert.Equal(14.122, ObdPids.TryDecode(ObdPids.Find(0x42)!, new byte[] { 0x41, 0x42, 0x37, 0x2A })!.Value, 3);
        Assert.Null(ObdPids.TryDecode(ObdPids.Find(0x0C)!, new byte[] { 0x7F, 0x01, 0x12 }));
        Assert.Null(ObdPids.TryDecode(ObdPids.Find(0x0C)!, new byte[] { 0x41, 0x0D, 1, 2 }));
    }

    [Fact]
    public async Task PollsPcmOverElm()
    {
        await using var sim = FordSimulator.Create();
        await using var elm = await ElmTransport.ConnectAsync(sim.Host, new ElmOptions { Profile = ElmProfiles.Elm327Clone });
        var pcm = new ModuleSession(elm, FordModules.Find("PCM")!, FordBus.HsCan);
        var rpm = ObdPids.TryDecode(ObdPids.Find(0x0C)!, await pcm.RequestAsync(new byte[] { 0x01, 0x0C }));
        Assert.InRange(rpm!.Value, 800, 3300);
    }

    [Fact]
    public void DtcStatusAndText()
    {
        Assert.Equal("Confirmed, Pending, Active", new DtcStatus(0x0D).Summary);
        Assert.Equal("History", new DtcStatus(0x00).Summary);
        Assert.Equal("System too lean (bank 1)", DtcDescriptions.Describe("P0171-00"));
        Assert.Null(DtcDescriptions.Describe("P1234"));
    }
}
