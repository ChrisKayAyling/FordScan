using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.App.Services;
using FordDiag.Comms;
using FordDiag.Comms.Serial;
using FordDiag.Core;
using FordDiag.Core.Capture;
using FordDiag.Core.Simulation;

namespace FordDiag.App.ViewModels;

public sealed record CaptureRow(string Time, string Module, string What, string Request, string Response, bool Flag, int Segment);
public sealed record SegmentChoice(int Index, string Label) { public override string ToString() => Label; }

/// <summary>
/// Records what another program (for example FORScan) says to the adapter, decodes it into UDS requests and drafts procedure
/// and data files from it.
/// </summary>
public sealed partial class CaptureViewModel : PageViewModel
{
    private readonly AppState _s;
    private CaptureLog _log = new();
    private TrafficProxy? _proxy;
    private FordSimulator? _sim;
    private IReadOnlyList<CaptureEvent>? _loaded;
    private DecodedCapture _decoded = new();

    public CaptureViewModel(AppState state)
    {
        _s = state;
        _selectedAdapter = FordAdapters.Find(state.Settings.AdapterKey) ?? FordAdapters.All[0];
        _port = state.Settings.Port;
        state.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppState.State)) { StartCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(StartHint)); } };
    }

    public override string Title => "Capture";
    public AppState State => _s;
    public IReadOnlyList<FordAdapter> Adapters => FordAdapters.All;
    public ObservableCollection<CaptureRow> Rows { get; } = new();
    public ObservableCollection<SegmentChoice> Segments { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();

    [ObservableProperty] private FordAdapter _selectedAdapter;
    [ObservableProperty] private string _port;
    [ObservableProperty] private decimal _listenPort = 35000;
    [ObservableProperty] private bool _allowRemote;
    [ObservableProperty] private bool _useSimulator;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsStopped))] private bool _isRunning;
    [ObservableProperty] private string _status = "Not recording";
    [ObservableProperty] private string _markerText = "";
    [ObservableProperty] private SegmentChoice? _selectedSegment;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private string _message = "";

    public bool IsStopped => !IsRunning;
    public bool HasRows => Rows.Count > 0;
    public int ActualPort => _proxy?.Port ?? 0;
    public string Instructions => $"In FORScan open Settings → Connection, choose \"WiFi\", enter 127.0.0.1 and port {(ActualPort > 0 ? ActualPort : (int)ListenPort)}, then connect and use FORScan as usual. Type a marker here before each action you want to turn into a procedure.";
    public string ExportFolder => System.IO.Path.Combine(AppSettings.ConfigDir, "service");

    partial void OnListenPortChanged(decimal value) => OnPropertyChanged(nameof(Instructions));
    partial void OnUseSimulatorChanged(bool value) { StartCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(StartHint)); }

    private bool CanStart() => !IsRunning && (UseSimulator || !_s.IsConnected);
    public string StartHint => !UseSimulator && _s.IsConnected ? "Disconnect on the Connect page first: only one program can use the adapter." : "";

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Message = "";
        _log = new CaptureLog();
        _log.Added += _ => { };
        try
        {
            if (UseSimulator)
            {
                _sim = FordSimulator.Create();
                _proxy = new TrafficProxy(_ => ValueTask.FromResult<ISerialLink>(_sim.Host), _log, (int)ListenPort, AllowRemote);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Port)) throw new ArgumentException("Choose the adapter's serial port first.");
                var profile = SelectedAdapter.Profile;
                _proxy = new TrafficProxy(ct => SerialLinkFactory.OpenAsync(Port, new SerialLinkOptions { BaudRate = profile.DefaultBaud, RtsCts = profile.RtsCts }, ct),
                    _log, (int)ListenPort, AllowRemote);
            }
            _proxy.ClientChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatus);
            await _proxy.StartAsync();
            IsRunning = true;
            _loaded = null;
            UpdateStatus();
            _s.Info($"Capture started on port {_proxy.Port}.");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or CommsException or ArgumentException or UnauthorizedAccessException)
        {
            Message = "Could not start: " + ex.Message;
            await TearDownAsync();
        }
        OnPropertyChanged(nameof(ActualPort)); OnPropertyChanged(nameof(Instructions));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        Refresh();
        await TearDownAsync();
        IsRunning = false;
        Status = $"Stopped · {_log.Count} lines recorded";
        StartCommand.NotifyCanExecuteChanged();
    }

    private async Task TearDownAsync()
    {
        if (_proxy is not null) await _proxy.DisposeAsync();
        if (_sim is not null) await _sim.DisposeAsync();
        _proxy = null; _sim = null;
    }

    private void UpdateStatus() => Status = _proxy is null ? "Not recording"
        : _proxy.HasClient ? $"Recording · program connected · port {_proxy.Port}" : $"Waiting for the program to connect on port {_proxy.Port}";

    [RelayCommand]
    private void AddMarker()
    {
        var text = MarkerText.Trim();
        if (text.Length == 0 || _loaded is not null) return;
        _log.Mark(text);
        MarkerText = "";
        Refresh();
    }

    /// <summary>Decodes what has been recorded so far and refreshes the lists. Called by a timer while recording.</summary>
    public void Refresh()
    {
        var events = _loaded ?? _log.Snapshot();
        _decoded = ElmTrafficDecoder.Decode(events);
        Rows.Clear();
        foreach (var x in _decoded.Exchanges.Where(e => e.Service != 0x3E))
            Rows.Add(new CaptureRow(x.Seconds.ToString("0.0") + " s", FordModules.Find(x.TxId.ToString("X3"))?.Abbrev ?? x.TxId.ToString("X3"),
                UdsAnnotator.Describe(x), x.RequestHex, x.ResponseHex, UdsAnnotator.IsWrite(x) || UdsAnnotator.IsSecurity(x), x.Segment));
        var keep = SelectedSegment?.Index;
        Segments.Clear();
        Segments.Add(new SegmentChoice(0, $"Before the first marker ({_decoded.Exchanges.Count(e => e.Segment == 0)})"));
        for (int i = 0; i < _decoded.Markers.Count; i++)
            Segments.Add(new SegmentChoice(i + 1, $"{_decoded.Markers[i].Label} ({_decoded.Exchanges.Count(e => e.Segment == i + 1)})"));
        SelectedSegment = Segments.FirstOrDefault(s => s.Index == keep) ?? (Segments.Count > 1 ? Segments[1] : Segments[0]);

        var sum = CaptureAnalysis.Summarize(_decoded);
        Summary = $"{sum.Exchanges} requests · {sum.Modules.Count} module(s)" + (sum.Vin is null ? "" : $" · VIN {sum.Vin}");
        var warns = new List<string>();
        if (sum.SecurityAccess) warns.Add("Security access was used: seeds and keys are recorded, but this tool cannot calculate keys, so those steps cannot be replayed.");
        if (sum.Programming) warns.Add("Programming (flashing) requests were seen. Never replay those.");
        Warning = string.Join(" ", warns);
        OnPropertyChanged(nameof(HasRows));
        ExportProcedureCommand.NotifyCanExecuteChanged();
    }

    private bool CanExport() => SelectedSegment is not null && _decoded.Exchanges.Any(e => e.Segment == SelectedSegment.Index && e.Positive && e.Service is 0x10 or 0x11 or 0x2E or 0x2F or 0x31 or 0x14);

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportProcedure()
    {
        try
        {
            var seg = SelectedSegment!;
            var name = seg.Index == 0 ? "Captured procedure" : _decoded.Markers[seg.Index - 1].Label;
            var vin = CaptureAnalysis.FindVin(_decoded);
            bool isTest = _decoded.Exchanges.Any(e => e.Segment == seg.Index && e.Service == 0x2F);
            var p = CaptureAnalysis.BuildProcedure(CaptureAnalysis.Segment(_decoded, seg.Index), name, vin, isTest ? FordDiag.Core.Service.ProcedureKind.OutputTest : FordDiag.Core.Service.ProcedureKind.Service);
            Directory.CreateDirectory(ExportFolder);
            var path = System.IO.Path.Combine(ExportFolder, p.Id + ".json");
            File.WriteAllText(path, CaptureAnalysis.ToJson(new[] { p }));
            Message = $"Saved {p.Steps.Count} step(s) to {path}. Restart FordDiag to see it on the Service page.";
            _s.Info(Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { Message = ex.Message; }
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        var path = await _s.Dialogs.PickSaveFileAsync("Save capture", "capture.jsonl", "Capture log", "*.jsonl");
        if (path is null) return;
        var log = new CaptureLog();
        foreach (var e in _loaded ?? _log.Snapshot()) log.Add(e.Dir, e.Text);
        log.Save(path);
        Message = "Saved " + path;
    }

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        var path = await _s.Dialogs.PickOpenFileAsync("Open capture or text trace", "Capture logs", "*.jsonl", "*.txt", "*.log");
        if (path is null) return;
        try { OpenText(path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? null : await File.ReadAllTextAsync(path), path); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { Message = "Could not read the file: " + ex.Message; }
    }

    /// <summary>Loads a saved capture (.jsonl when <paramref name="text"/> is null) or a plain text ELM trace.</summary>
    public void OpenText(string? text, string path)
    {
        _loaded = text is null ? CaptureLog.Load(path) : CaptureLog.FromText(text);
        Refresh();
        Status = $"Opened {System.IO.Path.GetFileName(path)}";
    }

    [RelayCommand]
    private void ExportPidDraft()
    {
        Directory.CreateDirectory(System.IO.Path.Combine(AppSettings.ConfigDir, "pids"));
        var path = System.IO.Path.Combine(AppSettings.ConfigDir, "pids", "captured-dids.json");
        File.WriteAllText(path, CaptureAnalysis.PidSetDraft(_decoded, "dids"));
        Message = $"Saved the DIDs seen ({CaptureAnalysis.DidInventory(_decoded).Count()}) to {path}. Fill in the scaling, then restart FordDiag.";
    }

    public override void OnLeave() { }
}
