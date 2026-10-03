using FordDiag.App.Services;
using FordDiag.App.ViewModels;
using FordDiag.Core;

namespace FordDiag.App.Tests;

public class ViewModelTests : IAsyncLifetime
{
    private readonly AutoDialogs _dialogs = new();
    private AppState _s = null!;
    private MainWindowViewModel _main = null!;
    private readonly string _home = Path.Combine(Path.GetTempPath(), "forddiag-vmtests-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _s = new AppState(_dialogs, new AppSettings());
        _main = new MainWindowViewModel(_s);
        await _s.ConnectAsync(FordAdapters.Find("vlinker-fs")!, null, simulator: true);
        await T<ModulesViewModel>().ScanCommand.ExecuteAsync(null);
    }

    public async Task DisposeAsync() => await _s.DisconnectAsync();

    private T T<T>() where T : PageViewModel => _main.NavItems.Select(n => n.Page).OfType<T>().Single();

    [Fact]
    public void ScanFillsModulesVinAndDtcCounts()
    {
        Assert.Equal(6, _s.Modules.Count);
        Assert.Equal(FordSimulatorVin, _s.Vin);
        Assert.Equal(1, _s.Modules.First(m => m.Abbrev == "PCM").DtcCount);
        Assert.Equal(0, _s.Modules.First(m => m.Abbrev == "BCM").DtcCount);
        Assert.Contains("2 trouble codes", T<ModulesViewModel>().Summary);
    }

    private static readonly string FordSimulatorVin = FordDiag.Core.Simulation.FordSimulator.Vin;

    [Fact]
    public async Task DtcReadDescribesAndClearNeedsExpert()
    {
        var dtc = T<DtcViewModel>();
        await dtc.ReadCommand.ExecuteAsync(null);
        Assert.Equal(2, dtc.Rows.Count);
        Assert.Contains(dtc.Rows, r => r.Code == "P0171-00" && r.Description.Contains("lean"));
        Assert.False(dtc.ClearCommand.CanExecute(null));
        _s.IsExpertMode = true;
        Assert.True(dtc.ClearCommand.CanExecute(null));
        await dtc.ClearCommand.ExecuteAsync(null);
        Assert.Empty(dtc.Rows);
        Assert.True(dtc.ShowEmpty);
    }

    [Fact]
    public async Task ClearDeclinedLeavesCodes()
    {
        var dtc = T<DtcViewModel>();
        _s.IsExpertMode = true; _dialogs.Answer = false;
        await dtc.ReadCommand.ExecuteAsync(null);
        await dtc.ClearCommand.ExecuteAsync(null);
        Assert.Equal(2, dtc.Rows.Count);
    }

    [Fact]
    public async Task LiveDataFillsTilesAndStops()
    {
        var live = T<LiveDataViewModel>();
        var run = live.StartCommand.ExecuteAsync(null);
        var rpmTile = live.Tiles.First(t => t.Pid.Pid == 0x0C);
        for (int i = 0; i < 100 && rpmTile.Points.Count <= 2; i++) await Task.Delay(50);
        live.StopCommand.Execute(null);
        await run;
        Assert.False(live.IsRunning);
        var rpm = live.Tiles.First(t => t.Pid.Pid == 0x0C);
        Assert.NotEqual("–", rpm.ValueText);
        Assert.True(rpm.Points.Count > 2);
    }

    [Fact]
    public async Task DidWriteFlowRequiresPreviewAndExpert()
    {
        var c = T<CodingViewModel>();
        c.SelectedModule = _s.Modules.First(m => m.Abbrev == "BCM");
        c.DidText = "DE01";
        await c.ReadDidCommand.ExecuteAsync(null);
        Assert.Equal("08 40 82 00 12", c.ValueText);
        Assert.False(c.WriteCommand.CanExecute(null));
        c.ValueText = "08 40 82 00 13";
        await c.PreviewCommand.ExecuteAsync(null);
        Assert.Contains("Dry run", c.Result);
        Assert.False(c.WriteCommand.CanExecute(null)); // still not expert
        _s.IsExpertMode = true;
        Assert.True(c.WriteCommand.CanExecute(null));
        c.ValueText = "08 40 82 00 14";               // editing invalidates the preview
        Assert.False(c.WriteCommand.CanExecute(null));
    }

    [Fact]
    public void AsBuiltEditingTracksChangesAndInvalidLines()
    {
        var c = T<CodingViewModel>();
        c.LoadText(ScreenshotRunner.SampleAsBuilt, "x.txt");
        Assert.Equal(5, c.Lines.Count);
        Assert.Equal(0, c.ChangedCount);
        c.Lines[0].BytesText = "FF" + c.Lines[0].OriginalText[2..];
        Assert.Equal(1, c.ChangedCount);
        Assert.True(c.Lines[0].ChecksumBad);
        c.FixChecksumsCommand.Execute(null);
        Assert.True(c.Lines[0].ChecksumOk);
        c.Lines[1].BytesText = "5E";                  // wrong length
        Assert.Equal(1, c.InvalidCount);
        Assert.False(c.SaveFileCommand.CanExecute(null));
        c.RevertAllCommand.Execute(null);
        Assert.Equal(0, c.ChangedCount + c.InvalidCount);
        Assert.True(c.SaveFileCommand.CanExecute(null));
    }

    [Fact]
    public async Task TerminalGatesWriteServicesAndAdapterConfig()
    {
        var t = T<TerminalViewModel>();
        t.Input = "2E DE 01 00 00 00 00 00"; await t.SendCommand.ExecuteAsync(null);
        Assert.Contains(_s.Log, l => l.Kind == LogKind.Error && l.Text.Contains("expert mode"));
        t.Input = "ATSP6"; await t.SendCommand.ExecuteAsync(null);
        Assert.Equal(2, _s.Log.Count(l => l.Kind == LogKind.Error && l.Text.Contains("expert mode")));
        t.Input = "22 F1 90"; await t.SendCommand.ExecuteAsync(null);
        Assert.True(_s.Log.Any(l => l.Kind == LogKind.Rx && l.Text.Contains("62 F1 90")), string.Join(" / ", _s.Log.Where(l => l.Kind != LogKind.Trace).Select(l => l.Kind + ":" + l.Text)));
    }

    [Fact]
    public async Task ExpertToggleAsksForConfirmation()
    {
        _dialogs.Answer = false;
        await _main.ToggleExpertCommand.ExecuteAsync(null);
        Assert.False(_s.IsExpertMode);
        _dialogs.Answer = true;
        await _main.ToggleExpertCommand.ExecuteAsync(null);
        Assert.True(_s.IsExpertMode);
        await _main.ToggleExpertCommand.ExecuteAsync(null);
        Assert.False(_s.IsExpertMode);
    }

    [Fact]
    public async Task DisconnectClearsEverything()
    {
        await _s.DisconnectAsync();
        Assert.Empty(_s.Modules);
        Assert.Null(_s.Vin);
        Assert.False(_s.IsConnected);
    }

    [Fact]
    public async Task AdapterWithoutMsCanSkipsMsModules()
    {
        await _s.ConnectAsync(FordAdapters.Find("elm327")!, null, simulator: true);
        await T<ModulesViewModel>().ScanCommand.ExecuteAsync(null);
        Assert.Equal(3, _s.Modules.Count);
        Assert.All(_s.Modules, m => Assert.Equal(FordBus.HsCan, m.Bus));
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var path = Path.Combine(_home, "s.json");
        var a = AppSettings.Load(path); a.Theme = "Dark"; a.Port = "COM3"; a.Save();
        var b = AppSettings.Load(path);
        Assert.Equal("Dark", b.Theme); Assert.Equal("COM3", b.Port);
        File.WriteAllText(path, "{ not json");
        Assert.Equal("System", AppSettings.Load(path).Theme); // corrupt file -> defaults
    }
}

public class ConfigurationTests : IAsyncLifetime
{
    private readonly AutoDialogs _dialogs = new();
    private AppState _s = null!;
    private MainWindowViewModel _main = null!;

    public async Task InitializeAsync()
    {
        _s = new AppState(_dialogs, new AppSettings());
        _main = new MainWindowViewModel(_s);
        await _s.ConnectAsync(FordAdapters.Find("vlinker-fs")!, null, simulator: true);
        await (_main.NavItems.Select(n => n.Page).OfType<ModulesViewModel>().Single()).ScanCommand.ExecuteAsync(null);
        Coding.SelectedModule = _s.Modules.First(m => m.Abbrev == "APIM");
    }

    public async Task DisposeAsync() => await _s.DisconnectAsync();

    private CodingViewModel Coding => _main.NavItems.Select(n => n.Page).OfType<CodingViewModel>().Single();
    private ConfigurationViewModel Cfg => Coding.Config;
    private ConfigFieldViewModel Field(string name) => Cfg.Rows.Where(r => r.Field is not null).Select(r => r.Field!).First(f => f.Name == name);

    [Fact]
    public async Task ReadShowsNamedOptions()
    {
        await Cfg.ReadCommand.ExecuteAsync(null);
        Assert.True(Cfg.HasDefinition);
        Assert.Contains("SYNC 3", Cfg.DefinitionText);
        Assert.Equal("RVC Present", Field("Rear Camera").SelectedOption!.Label);
        Assert.Equal(0, Cfg.ChangeCount);
        Assert.Contains(Cfg.Rows, r => r.IsHeader && r.Title.Contains("DE00"));
    }

    [Fact]
    public async Task EditingAnOptionTracksChangesAndRevert()
    {
        await Cfg.ReadCommand.ExecuteAsync(null);
        var cam = Field("Rear Camera");
        cam.SelectedOption = cam.Options.First(o => o.Value == 0);
        Assert.Equal(1, Cfg.ChangeCount);
        Assert.True(cam.IsChanged);
        Cfg.RevertAllCommand.Execute(null);
        Assert.Equal(0, Cfg.ChangeCount);
        Assert.Equal("RVC Present", Field("Rear Camera").SelectedOption!.Label);
    }

    [Fact]
    public async Task SearchFiltersByNameAndOptionText()
    {
        await Cfg.ReadCommand.ExecuteAsync(null);
        int all = Cfg.Rows.Count;
        Cfg.Search = "camera";
        Assert.InRange(Cfg.Rows.Count, 2, all - 1);
        Assert.All(Cfg.Rows.Where(r => r.IsField), r => Assert.True(
            r.Field!.Name.Contains("camera", StringComparison.OrdinalIgnoreCase) || r.Field.Options.Any(o => o.Label.Contains("camera", StringComparison.OrdinalIgnoreCase))));
        Cfg.Search = "";
        Assert.Equal(all, Cfg.Rows.Count);
    }

    [Fact]
    public async Task ApplyNeedsExpertAndWritesOnlyChangedBlocks()
    {
        await Cfg.ReadCommand.ExecuteAsync(null);
        var cam = Field("Rear Camera");
        cam.SelectedOption = cam.Options.First(o => o.Value == 0);
        Assert.False(Cfg.ApplyCommand.CanExecute(null));      // not in expert mode
        _s.IsExpertMode = true;
        Assert.True(Cfg.ApplyCommand.CanExecute(null));
        _dialogs.Answer = false;
        await Cfg.ApplyCommand.ExecuteAsync(null);             // declined: nothing written
        Assert.Equal(0x0A, ((FordDiag.Core.Simulation.FordSimulator)SimOf(_s)).Modules["APIM"].Dids[0xDE00][0]);
        _dialogs.Answer = true;
        await Cfg.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("verified", Cfg.Message);
        Assert.Equal(0x08, ((FordDiag.Core.Simulation.FordSimulator)SimOf(_s)).Modules["APIM"].Dids[0xDE00][0]);
        Assert.Equal(0, Cfg.ChangeCount);                      // reloaded as the new baseline
    }

    private static object SimOf(AppState s) =>
        typeof(AppState).GetField("_sim", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(s)!;

    private static string TextFor(uint module)
    {
        var img = new FordDiag.Core.Coding.AsBuiltImage { Module = module };
        img.Blocks[1] = new byte[] { 0x00, 0x40, 0x82, 0x00, 0x12, 0x5E, 0x09, 0x10, 0x00, 0x33 };
        img.Blocks[2] = new byte[] { 0x0A, 0x12, 0x34, 0x56, 0x78 };
        return img.ToText();
    }

    [Fact]
    public void OpensFileAndFallsBackToRawBytesWithoutDefinition()
    {
        Cfg.OpenText(TextFor(0x7F0), "unknown.txt");      // no definitions exist for this address
        Assert.True(Cfg.HasData);
        Assert.False(Cfg.HasDefinition);
        Assert.True(Cfg.ShowRawNotice);
        var first = Cfg.Rows.First(r => r.IsField).Field!;
        Assert.Equal("Byte 0", first.Name);
        first.TextValue = "FF";
        Assert.Equal(1, Cfg.ChangeCount);
        // saved text has valid checksums even though the edit changed the data
        var saved = AsBuiltData.Parse(Cfg.SaveText(), out var errs);
        Assert.Empty(errs); Assert.Empty(saved.VerifyChecksums());
        Assert.Contains("7F0-01-01 FF 40", saved.Format());
    }

    [Fact]
    public void BcmFileGetsNamedOptionsAndADefinitionChoice()
    {
        Coding.SelectedModule = _s.Modules.First(m => m.Abbrev == "BCM");
        Cfg.OpenText(TextFor(0x726), "bcm.txt");
        Assert.True(Cfg.HasDefinition);
        Assert.True(Cfg.ShowChoices);                      // the matching definition plus "None (raw bytes)"
        Assert.Contains("CyanLabs", Cfg.Attribution);
        var prime = Cfg.Rows.Where(r => r.Field is not null).Select(r => r.Field!).First(f => f.Name.StartsWith("Fuel Prime"));
        Assert.Equal("No Prime", prime.SelectedOption!.Label);
        prime.SelectedOption = prime.Options.First(o => o.Value == 1);
        Assert.Equal(1, Cfg.ChangeCount);
        // switching to raw bytes keeps the edit (same working data, same baseline)
        Cfg.SelectedChoice = Cfg.Choices.Last();
        Assert.False(Cfg.HasDefinition);
        Assert.Equal(1, Cfg.ChangeCount);
        Assert.Equal("Byte 0", Cfg.Rows.First(r => r.IsField).Field!.Name);
    }

    [Fact]
    public async Task ReadingTheBcmPicksTheDefinitionByPartNumber()
    {
        Coding.SelectedModule = _s.Modules.First(m => m.Abbrev == "BCM");
        await Cfg.ReadCommand.ExecuteAsync(null);
        Assert.Equal("BCM (2011-2019)", Cfg.SelectedChoice!.Definition!.Name);   // module strategy BC3T-...
        var wake = Cfg.Rows.Where(r => r.Field is not null).Select(r => r.Field!).First(f => f.Name.StartsWith("PCM Wake"));
        Assert.Equal("Wake", wake.SelectedOption!.Label);
    }

    [Fact]
    public void CompareListsNamedDifferencesAndCanTakeValues()
    {
        var def = FordDiag.Core.Coding.DefinitionLibrary.Load().Single(d => d.Id == "apim-sync3");
        var img = new FordDiag.Core.Coding.AsBuiltImage { Module = 0x7D0 };
        img.Blocks[1] = new byte[10]; img.Blocks[2] = new byte[12];
        Cfg.OpenText(img.ToText(), "a.txt");
        Assert.True(Cfg.HasDefinition);
        var other = img.Clone();
        def.Block(1)!.Fields.Single(f => f.Name == "Rear Camera").WriteRaw(other.Blocks[1], 1);
        Cfg.Compare(other.ToText(), "b.txt");
        Assert.Contains(Cfg.Differences, d => d.StartsWith("Rear Camera") && d.Contains("RVC Present"));
        Cfg.TakeFileValuesCommand.Execute(null);
        Assert.Equal(1, Cfg.ChangeCount);
        Assert.Equal("RVC Present", Field("Rear Camera").SelectedOption!.Label);
    }
}

public class VehicleAndServiceTests : IAsyncLifetime
{
    private readonly AutoDialogs _dialogs = new();
    private AppState _s = null!;
    private MainWindowViewModel _main = null!;

    public async Task InitializeAsync()
    {
        _s = new AppState(_dialogs, new AppSettings());
        _main = new MainWindowViewModel(_s);
        await _s.ConnectAsync(FordAdapters.Find("vlinker-fs")!, null, simulator: true);
        await P<ModulesViewModel>().ScanCommand.ExecuteAsync(null);
    }
    public async Task DisposeAsync() => await _s.DisconnectAsync();

    private T P<T>() where T : PageViewModel => _main.NavItems.Select(n => n.Page).OfType<T>().Single();
    private FordDiag.Core.Simulation.FordSimulator Sim() =>
        (FordDiag.Core.Simulation.FordSimulator)typeof(AppState).GetField("_sim", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_s)!;

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    // ---- vehicle identification ----
    [Fact]
    public void ScanDecodesTheVinOffline()
    {
        Assert.True(_s.HasVehicle);
        Assert.Contains("Ford (truck)", _s.VehicleTitle);
        Assert.Contains("model year 2020", _s.VehicleTitle);
        Assert.Contains("United States", _s.VehicleSubtitle);
        Assert.Contains("check digit OK", _s.VehicleSubtitle);
    }

    [Fact]
    public async Task OnlineLookupFillsInModelAndPicksPidSet()
    {
        _s.VinClient = new FordDiag.Core.Vehicle.NhtsaVinClient(new HttpClient(new StubHandler(
            """{"Results":[{"Make":"FORD","Model":"F-150","ModelYear":"2019","Trim":"XLT","DisplacementL":"3.5","EngineCylinders":"6","ErrorCode":"0"}]}""")));
        await P<ModulesViewModel>().LookupVehicleCommand.ExecuteAsync(null);
        Assert.Equal("2019 FORD F-150 XLT", _s.VehicleTitle);
        Assert.Equal(2019, _s.VehicleYear);
        var live = P<LiveDataViewModel>();
        live.ShowEnhancedCommand.Execute(null);
        Assert.Equal("obdb-Ford-F-150", live.Enhanced.SelectedSet!.Id);
        Assert.Contains("Matched", live.Enhanced.SetHint);
    }

    [Fact]
    public async Task LookupFailureIsReportedNotThrown()
    {
        _s.VinClient = new FordDiag.Core.Vehicle.NhtsaVinClient(new HttpClient(new StubHandler("not json")));
        await P<ModulesViewModel>().LookupVehicleCommand.ExecuteAsync(null);
        Assert.NotNull(_s.LookupError);
        Assert.Null(_s.VehicleDetails);
    }

    // ---- module data ----
    private EnhancedViewModel Enhanced()
    {
        var live = P<LiveDataViewModel>();
        live.ShowEnhancedCommand.Execute(null);
        live.Enhanced.SelectedSet = live.Enhanced.Sets.Single(s => s.Id == "obdb-Ford-F-150");
        return live.Enhanced;
    }

    [Fact]
    public void ModuleListAndSearch()
    {
        var e = Enhanced();
        Assert.Equal(0x7E0u, e.SelectedModule!.Header);
        Assert.Contains(e.ModuleChoices, c => c.Header == 0x726 && c.Label.Contains("BCM") && c.Label.Contains("✓"));   // scanned
        int all = e.Signals.Count;
        Assert.True(all > 20);
        e.Search = "coolant";
        Assert.InRange(e.Signals.Count, 1, all - 1);
        Assert.All(e.Signals, s => Assert.True(s.Name.Contains("coolant", StringComparison.OrdinalIgnoreCase) || s.Group.Contains("coolant", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task SelectedValuesStreamFromTheSimulator()
    {
        var e = Enhanced();
        foreach (var sig in e.Signals.Take(3)) sig.IsSelected = true;
        Assert.Equal(3, e.Tiles.Count);
        var run = e.StartCommand.ExecuteAsync(null);
        for (int i = 0; i < 200 && e.Tiles.Any(t => t.ValueText == "–"); i++) await Task.Delay(25);
        e.Stop();
        await run;
        Assert.False(e.IsRunning);
        Assert.All(e.Tiles, t => { Assert.NotEqual("–", t.ValueText); Assert.False(t.Failed); });
    }

    [Fact]
    public void SelectionIsCappedAndExperimentalSignalsAreHiddenByDefault()
    {
        var e = Enhanced();
        Assert.All(e.Signals, s => Assert.False(s.Experimental));
        foreach (var sig in e.Signals.Take(EnhancedViewModel.MaxSelected + 5)) sig.IsSelected = true;
        Assert.Equal(EnhancedViewModel.MaxSelected, e.Tiles.Count);
    }

    [Fact]
    public async Task UnsupportedRequestsAreMarkedNotSupported()
    {
        var e = Enhanced();
        e.SelectedModule = e.ModuleChoices.First(c => c.Header == 0x7E0);
        // the simulated PCM answers only the catalog entries it knows; request one it does not have via another module
        var other = e.ModuleChoices.First(c => !_s.Modules.Any(m => m.Module.RequestId == c.Header));
        e.SelectedModule = other;
        e.Signals.First().IsSelected = true;
        var run = e.StartCommand.ExecuteAsync(null);
        for (int i = 0; i < 400 && !e.Tiles.All(t => t.Failed); i++) await Task.Delay(25);
        e.Stop(); await run;
        Assert.All(e.Tiles, t => Assert.True(t.Failed));
    }

    // ---- service functions and output tests ----
    [Fact]
    public void ServiceListShowsFunctionsAndTestsSeparately()
    {
        var sv = P<ServiceViewModel>();
        sv.Initialise();
        Assert.Contains(sv.Items, p => p.Id == "bms-reset");
        Assert.DoesNotContain(sv.Items, p => p.Kind == FordDiag.Core.Service.ProcedureKind.OutputTest);
        sv.ShowTestsTabCommand.Execute(null);
        Assert.Contains(sv.Items, p => p.Id == "obd2-mode08-support");
    }

    [Fact]
    public async Task PreviewSendsNothingAndRunNeedsExpertAndConfirmation()
    {
        var sv = P<ServiceViewModel>();
        sv.Initialise();
        sv.Selected = sv.Items.Single(p => p.Id == "bms-reset");
        Assert.True(sv.HasWarnings); Assert.True(sv.HasManual); Assert.Contains("not verified", sv.Badge);
        Assert.True(sv.PreviewCommand.CanExecute(null));
        await sv.PreviewCommand.ExecuteAsync(null);
        Assert.Contains("Dry run", sv.Result);
        Assert.Empty(Sim().Modules["BCM"].Log);

        Assert.False(sv.RunCommand.CanExecute(null));            // expert mode off
        _s.IsExpertMode = true;
        Assert.True(sv.RunCommand.CanExecute(null));
        _dialogs.Answer = false;
        await sv.RunCommand.ExecuteAsync(null);
        Assert.Empty(Sim().Modules["BCM"].Log);                  // declined
        _dialogs.Answer = true;
        await sv.RunCommand.ExecuteAsync(null);
        Assert.True(sv.ResultOk, sv.Result);
        Assert.Contains("BMS reset", Sim().Modules["BCM"].Log);
    }

    [Fact]
    public async Task GenericProceduresUseTheChosenModule()
    {
        var sv = P<ServiceViewModel>();
        sv.Initialise();
        sv.Selected = sv.Items.Single(p => p.Id == "reset-module-hard");
        Assert.True(sv.NeedsTarget);
        sv.SelectedTarget = _s.Modules.First(m => m.Abbrev == "ABS");
        _s.IsExpertMode = true;
        await sv.RunCommand.ExecuteAsync(null);
        Assert.True(sv.ResultOk, sv.Result);
        Assert.Contains("11 01", Sim().Modules["ABS"].Log);
    }

    [Fact]
    public async Task OutputTestRunsOnTheEngineModule()
    {
        var sv = P<ServiceViewModel>();
        sv.Initialise();
        sv.ShowTestsTabCommand.Execute(null);
        sv.Selected = sv.Items.Single(p => p.Id == "obd2-mode08-support");
        await sv.PreviewCommand.ExecuteAsync(null);              // preview is allowed without expert mode
        Assert.Contains("Dry run", sv.Result);
        _s.IsExpertMode = true;
        await sv.RunCommand.ExecuteAsync(null);
        Assert.True(sv.ResultOk, sv.Result);
        Assert.Contains(sv.Output, l => l.Contains("48 00"));
    }
}

public class CaptureTests
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "fd-cap-" + Guid.NewGuid().ToString("N"));

    private (CaptureViewModel vm, AppState s) Make()
    {
        Environment.SetEnvironmentVariable("FORDDIAG_HOME", _home);
        var s = new AppState(new AutoDialogs(), new AppSettings());
        var main = new MainWindowViewModel(s);
        return (main.NavItems.Select(n => n.Page).OfType<CaptureViewModel>().Single(), s);
    }

    [Fact]
    public async Task RecordsAProgramTalkingToTheSimulatedCarAndExportsAProcedure()
    {
        var (vm, _) = Make();
        vm.UseSimulator = true; vm.ListenPort = 0;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning, vm.Message);
        Assert.Contains("Waiting", vm.Status);

        var link = await FordDiag.Comms.Serial.TcpSerialLink.ConnectAsync("127.0.0.1", vm.ActualPort, TimeSpan.FromSeconds(5));
        await using (var client = await FordDiag.Comms.Elm.ElmTransport.ConnectAsync(link, new FordDiag.Comms.Elm.ElmOptions { Profile = FordDiag.Comms.Elm.ElmProfiles.Elm327Clone }))
        {
            var pcm = new FordDiag.Core.ModuleSession(client, FordDiag.Core.FordModules.Find("PCM")!, FordDiag.Core.FordBus.HsCan);
            await pcm.ReadDidAsync(0xF190);
            vm.MarkerText = "battery reset"; vm.AddMarkerCommand.Execute(null);
            var bcm = new FordDiag.Core.ModuleSession(client, FordDiag.Core.FordModules.Find("BCM")!, FordDiag.Core.FordBus.MsCan);
            var bms = FordDiag.Core.Service.ProcedureLibrary.Load().Single(p => p.Id == "bms-reset");
            Assert.True((await FordDiag.Core.Service.ProcedureRunner.RunAsync(bcm, bms, new FordDiag.Core.Service.ProcedureOptions { Commit = true })).Success);
        }
        await Task.Delay(150);
        vm.Refresh();
        Assert.Contains("VIN", vm.Summary);
        Assert.Contains(vm.Rows, r => r.Module == "BCM" && r.What.Contains("Routine 201A") && r.Segment == 1);
        Assert.Contains(vm.Segments, sg => sg.Label.StartsWith("battery reset (3)"));
        vm.SelectedSegment = vm.Segments.First(sg => sg.Index == 1);
        Assert.True(vm.ExportProcedureCommand.CanExecute(null));
        vm.ExportProcedureCommand.Execute(null);
        var file = Path.Combine(vm.ExportFolder, "captured-battery-reset.json");
        Assert.True(File.Exists(file), vm.Message);
        var back = FordDiag.Core.Service.ProcedureLibrary.ParseJson(File.ReadAllText(file)).Single();
        Assert.Equal("captured", back.Confidence);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void OpeningATextTraceFillsTheListsAndWarnsAboutSecurityAccess()
    {
        var (vm, _) = Make();
        vm.OpenText("""
            > ATCAF0
            < OK
            > ATSH 7E0
            < OK
            > 022701
            < 06 67 01 AA BB CC DD
            > 066 EE EE EE EE
            """.Replace("> 066 EE EE EE EE", "> 062702112233441\n< 02 67 02"), "x.txt");
        Assert.True(vm.HasRows);
        Assert.Contains("Security access", vm.Warning);
        Assert.Contains(vm.Rows, r => r.What.Contains("request seed") && r.Flag);
    }

    [Fact]
    public async Task CannotStartWhileTheAdapterIsInUse()
    {
        var (vm, s) = Make();
        await s.ConnectAsync(FordAdapters.Find("vlinker-fs")!, null, simulator: true);
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.UseSimulator = true;
        Assert.True(vm.StartCommand.CanExecute(null));
        await s.DisconnectAsync();
    }
}
