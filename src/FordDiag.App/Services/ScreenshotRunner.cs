using System.Net.Http;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FordDiag.App.ViewModels;
using FordDiag.App.Views;

namespace FordDiag.App.Services;

/// <summary>Dialogs that answer immediately (headless runs and tests).</summary>
public sealed class AutoDialogs : IDialogService
{
    public bool Answer { get; set; } = true;
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false) => Task.FromResult(Answer);
    public Task<string?> PickOpenFileAsync(string title, string filterName, params string[] patterns) => Task.FromResult<string?>(null);
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, string filterName, params string[] patterns) => Task.FromResult<string?>(null);
}

/// <summary>`FordDiag --screenshots dir`: drives the real window + view-models against the built-in simulator and saves PNGs.</summary>
/// <summary>Stands in for vpic.nhtsa.dot.gov in screenshots and tests.</summary>
internal sealed class StubVpic : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"Results":[{"Make":"FORD","Model":"F-150","ModelYear":"2020","Trim":"XLT","BodyClass":"Pickup","DriveType":"4WD/4-Wheel Drive/4x4","DisplacementL":"3.5","EngineCylinders":"6","FuelTypePrimary":"Gasoline","EngineModel":"EcoBoost","ErrorCode":"0"}]}""") });
}

internal static class ScreenshotRunner
{
    /// <summary>Example BCM-style as-built text with valid checksums (not from a real vehicle).</summary>
    public static string SampleAsBuilt { get; } = BuildSample();

    private static string BuildSample()
    {
        var img = new FordDiag.Core.Coding.AsBuiltImage { Module = 0x726 };
        img.Blocks[1] = new byte[] { 0x08, 0x40, 0x82, 0x00, 0x12, 0x5E, 0x09, 0x10, 0x00, 0x33 };
        img.Blocks[2] = new byte[] { 0x0A, 0x12, 0x34, 0x56, 0x78, 0xFF, 0x00, 0x00, 0x00, 0x11 };
        img.Blocks[3] = new byte[] { 0x4C, 0x00, 0x88, 0x00, 0x25 };
        return img.ToText();
    }

    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("FORDDIAG_HOME", Path.Combine(Path.GetTempPath(), "forddiag-shots-" + Environment.ProcessId));
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
        int code = 0;
        try { Scenario(dir); }
        catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
        Environment.Exit(code); // background reader threads of the serial layer would keep the process alive
        return code;
    }

    /// <summary>Plays the part of FORScan: reads the VIN, runs the battery reset and restarts the ABS module through the capture proxy.</summary>
    private static async Task RunClientThroughProxy(CaptureViewModel cap)
    {
        var link = await FordDiag.Comms.Serial.TcpSerialLink.ConnectAsync("127.0.0.1", cap.ActualPort, TimeSpan.FromSeconds(5));
        await using var client = await FordDiag.Comms.Elm.ElmTransport.ConnectAsync(link, new FordDiag.Comms.Elm.ElmOptions { Profile = FordDiag.Comms.Elm.ElmProfiles.Elm327Clone });
        var lib = FordDiag.Core.Service.ProcedureLibrary.Load();
        await new FordDiag.Core.ModuleSession(client, FordDiag.Core.FordModules.Find("PCM")!, FordDiag.Core.FordBus.HsCan).ReadDidAsync(0xF190);
        cap.MarkerText = "battery reset"; cap.AddMarkerCommand.Execute(null);
        await FordDiag.Core.Service.ProcedureRunner.RunAsync(new FordDiag.Core.ModuleSession(client, FordDiag.Core.FordModules.Find("BCM")!, FordDiag.Core.FordBus.MsCan),
            lib.Single(p => p.Id == "bms-reset"), new FordDiag.Core.Service.ProcedureOptions { Commit = true });
        cap.MarkerText = "restart ABS"; cap.AddMarkerCommand.Execute(null);
        await FordDiag.Core.Service.ProcedureRunner.RunAsync(new FordDiag.Core.ModuleSession(client, FordDiag.Core.FordModules.Find("ABS")!, FordDiag.Core.FordBus.HsCan),
            lib.Single(p => p.Id == "reset-module-hard"), new FordDiag.Core.Service.ProcedureOptions { Commit = true });
    }

    private static void Pump(int ms = 0)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); if (ms > 0) Thread.Sleep(10); } while (DateTime.UtcNow < until);
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static void Await(Task t)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!t.IsCompleted) { Pump(); Thread.Sleep(2); if (sw.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("UI task did not finish"); }
        t.GetAwaiter().GetResult();
    }

    private static void Shot(MainWindow w, string dir, string name)
    {
        Pump(60);
        var path = Path.Combine(dir, name + ".png");
        w.CaptureRenderedFrame()!.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine(path);
    }

    private static void Scenario(string dir)
    {
        var settings = new AppSettings { Path = null, Theme = "Light" };
        var window = new MainWindow { Width = 1280, Height = 800 };
        var state = new AppState(new AutoDialogs(), settings);
        var main = new MainWindowViewModel(state);
        window.DataContext = main;
        ThemeService.Apply("Light");
        window.Show();
        Pump(100);

        T Page<T>() where T : PageViewModel => main.NavItems.Select(n => n.Page).OfType<T>().Single();
        var connect = Page<ConnectViewModel>();
        var modules = Page<ModulesViewModel>();
        var dtc = Page<DtcViewModel>();
        var live = Page<LiveDataViewModel>();
        var service = Page<ServiceViewModel>();
        var coding = Page<CodingViewModel>();
        var term = Page<TerminalViewModel>();

        Shot(window, dir, "01-connect");
        connect.UseSimulator = true;
        connect.Adapter = FordDiag.Core.FordAdapters.Find("vlinker-fs")!;
        main.Navigate("Connect");
        Await(connect.ConnectCommand.ExecuteAsync(null));   // navigates to Modules on success
        main.Navigate("Connect");
        Shot(window, dir, "02-connected");

        main.Navigate("Modules");
        Await(modules.ScanCommand.ExecuteAsync(null));
        state.VinClient = new FordDiag.Core.Vehicle.NhtsaVinClient(new HttpClient(new StubVpic()));
        Await(modules.LookupVehicleCommand.ExecuteAsync(null));
        Shot(window, dir, "03-modules");

        main.Navigate("Trouble codes");
        Await(dtc.ReadCommand.ExecuteAsync(null));
        Shot(window, dir, "04-trouble-codes");

        ThemeService.Apply("Dark");
        main.Navigate("Live data");
        _ = live.StartCommand.ExecuteAsync(null);
        Pump(1800);
        Shot(window, dir, "05-live-data-dark");
        live.StopCommand.Execute(null);
        Pump(300);
        ThemeService.Apply("Light");

        // Ford module data: signals of the matched F-150 set
        live.ShowEnhancedCommand.Execute(null);
        var enh = live.Enhanced;
        enh.IncludeExperimental = false;
        foreach (var name in new[] { "Engine speed", "Coolant", "Boost", "Throttle", "Intake air", "Fuel" })
            foreach (var sg in enh.Signals.Where(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(1)) sg.IsSelected = true;
        if (enh.Tiles.Count < 4) foreach (var sg in enh.Signals.Where(x => !x.IsSelected).Take(6 - enh.Tiles.Count)) sg.IsSelected = true;
        _ = enh.StartCommand.ExecuteAsync(null);
        Pump(1800);
        Shot(window, dir, "06-module-data");
        enh.Stop();
        Pump(300);
        live.ShowStandardCommand.Execute(null);

        // service functions and output tests
        main.Navigate("Service");
        service.Selected = service.Items.First(p => p.Id == "bms-reset");
        Shot(window, dir, "07-service-bms");
        state.IsExpertMode = true;
        Await(service.RunCommand.ExecuteAsync(null));
        Shot(window, dir, "08-service-result");
        state.IsExpertMode = false;
        service.ShowTestsTabCommand.Execute(null);
        Await(service.PreviewCommand.ExecuteAsync(null));
        Shot(window, dir, "09-output-tests");

        // capture: another program (here a FordDiag client standing in for FORScan) talks to the simulated car through the proxy
        main.Navigate("Capture");
        var cap = Page<CaptureViewModel>();
        cap.UseSimulator = true; cap.ListenPort = 0;
        Await(cap.StartCommand.ExecuteAsync(null));
        Await(RunClientThroughProxy(cap));
        Pump(300);
        cap.Refresh();
        cap.SelectedSegment = cap.Segments.FirstOrDefault(sg => sg.Index == 1) ?? cap.Segments[0];
        Shot(window, dir, "10-capture");
        Await(cap.StopCommand.ExecuteAsync(null));

        main.Navigate("Coding");
        coding.SelectedModule = state.Modules.First(m => m.Abbrev == "APIM");
        coding.SelectedTab = 0;
        Shot(window, dir, "11-coding-empty");
        Await(coding.Config.ReadCommand.ExecuteAsync(null));
        Shot(window, dir, "12-coding-configuration");
        coding.Config.Search = "camera";
        Shot(window, dir, "13-coding-search");
        coding.Config.Search = "";
        var cfg = coding.Config;
        foreach (var row in cfg.Rows.Where(r => r.Field is not null).Take(60))
        {
            var f = row.Field!;
            if (f.Name == "Rear Camera") f.SelectedOption = f.Options.First(o => o.Value == 0);
            if (f.Name == "PDC HMI") f.SelectedOption = f.Options.First(o => o.Value == 0);
        }
        coding.Config.Search = "";
        state.IsExpertMode = true;
        Shot(window, dir, "14-coding-changes");
        state.IsExpertMode = false;

        // BCM: option names from the CyanLabs database, picked by the module's part number
        coding.SelectedModule = state.Modules.First(m => m.Abbrev == "BCM");
        Await(coding.Config.ReadCommand.ExecuteAsync(null));
        foreach (var row in coding.Config.Rows.Where(r => r.Field is not null))
            if (row.Field!.Name.StartsWith("Fuel Prime")) row.Field.SelectedOption = row.Field.Options.First(o => o.Value == 0);
        Shot(window, dir, "15-coding-bcm");

        coding.SelectedTab = 1;
        coding.LoadText(SampleAsBuilt, "/home/user/bcm-asbuilt.txt");
        coding.Lines[1].BytesText = "5E 09 10 00 33 00";
        coding.Lines[3].BytesText = "FF00 00";
        Shot(window, dir, "16-coding-lines");

        state.IsExpertMode = true;
        coding.SelectedModule = state.Modules.First(m => m.Abbrev == "BCM");
        coding.SelectedTab = 2;
        coding.DidText = "DE01";
        Await(coding.ReadDidCommand.ExecuteAsync(null));
        coding.ValueText = "08 40 82 00 13";
        Await(coding.PreviewCommand.ExecuteAsync(null));
        Shot(window, dir, "17-coding-did");
        state.IsExpertMode = false;

        main.Navigate("Terminal");
        foreach (var c in new[] { "ATRV", "22 F1 90", "22 F1 88", "19 02 AF", "2E DE 01 00" })
        { term.Input = c; Await(term.SendCommand.ExecuteAsync(null)); }
        Shot(window, dir, "18-terminal");

        main.Navigate("Settings");
        Shot(window, dir, "19-settings");

        ThemeService.Apply("Dark");
        main.Navigate("Modules");
        Shot(window, dir, "20-modules-dark");
        Await(state.DisconnectAsync());
        window.Close();
    }
}
