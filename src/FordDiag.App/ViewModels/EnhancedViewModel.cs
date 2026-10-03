using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.App.Services;
using FordDiag.Comms;
using FordDiag.Core;
using FordDiag.Core.Live;

namespace FordDiag.App.ViewModels;

public sealed partial class EnhancedSignalViewModel : ObservableObject
{
    public EnhancedSignalViewModel(PidCommand command, PidSignal signal) { Command = command; Signal = signal; }
    public PidCommand Command { get; }
    public PidSignal Signal { get; }
    public string Name => Signal.Name;
    public string Group => Signal.Group;
    public string Unit => Signal.UnitSymbol;
    public bool Experimental => Command.Experimental;
    public string Request => $"{Command.Header:X3} · 22 {Command.Did:X4}";
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class EnhancedTile : ObservableObject
{
    private const int History = 60;
    private readonly List<double> _samples = new();
    public EnhancedTile(EnhancedSignalViewModel source) { Source = source; }
    public EnhancedSignalViewModel Source { get; }
    public string Name => Source.Name;
    public string Unit => Source.Unit;
    public string Module => FordModules.Find(Source.Command.Header.ToString("X3"))?.Abbrev ?? Source.Command.Header.ToString("X3");
    [ObservableProperty] private string _valueText = "–";
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private IList<Point> _points = new List<Point> { new(0, 34), new(190, 34) };

    public void Update(string text, double? value)
    {
        Failed = false; ValueText = text;
        if (value is null) return;
        _samples.Add(value.Value);
        if (_samples.Count > History) _samples.RemoveAt(0);
        double lo = _samples.Min(), hi = _samples.Max(), span = Math.Max(1e-9, hi - lo);
        Points = _samples.Select((v, i) => new Point(i * 190.0 / (History - 1), hi == lo ? 17 : 33 - (v - lo) / span * 30)).ToList();
    }

    public void MarkFailed(string text) { Failed = true; ValueText = text; }
}

public sealed record ModuleChoice(uint Header, string Label) { public override string ToString() => Label; }

/// <summary>Reads Ford mode 22 data (OBDb signal sets) from any module and shows the chosen signals live.</summary>
public sealed partial class EnhancedViewModel : ObservableObject
{
    public const int MaxSelected = 24;
    private readonly AppState _s;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<PidSet>? _sets;
    private readonly List<EnhancedSignalViewModel> _all = new();

    public EnhancedViewModel(AppState state)
    {
        _s = state;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.State)) { if (!_s.IsConnected) Stop(); StartCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(Hint)); }
            if (e.PropertyName is nameof(AppState.VehicleDetails) or nameof(AppState.Vin)) AutoSelect();
        };
        state.Modules.CollectionChanged += (_, _) => RebuildModules();
    }

    public IReadOnlyList<PidSet> Sets => _sets ??= PidCatalog.Load(Path.Combine(AppSettings.ConfigDir, "pids"), w => _s.Error("PID file skipped: " + w));
    public ObservableCollection<ModuleChoice> ModuleChoices { get; } = new();
    public ObservableCollection<EnhancedSignalViewModel> Signals { get; } = new();
    public ObservableCollection<EnhancedTile> Tiles { get; } = new();

    [ObservableProperty] private PidSet? _selectedSet;
    [ObservableProperty] private ModuleChoice? _selectedModule;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _includeExperimental;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _isRunning;
    [ObservableProperty] private string _rate = "";
    [ObservableProperty] private string _setHint = "";

    public bool IsStopped => !IsRunning;
    public int SelectedCount => Tiles.Count;
    public string Attribution => SelectedSet?.Attribution ?? "";
    public bool HasSet => SelectedSet is not null;
    public string Hint => !_s.IsConnected ? "Connect to a vehicle first." : SelectedSet is null ? "Choose the vehicle model." : $"Tick up to {MaxSelected} values, then press Start.";

    public void Initialise() { if (SelectedSet is null) AutoSelect(); }

    private void AutoSelect()
    {
        var best = PidCatalog.SetsFor(Sets, _s.VehicleModel, _s.VehicleYear).FirstOrDefault();
        if (best is null) return;
        SelectedSet = best;
        SetHint = _s.VehicleModel is null
            ? "Using the generic Ford list. Look up the vehicle on the Modules page to match your model."
            : $"Matched to {_s.VehicleDetails?.Title}.";
    }

    partial void OnSelectedSetChanged(PidSet? value)
    {
        Stop(); Tiles.Clear(); _all.Clear();
        if (value is not null)
            foreach (var c in value.Commands.Where(c => c.AppliesTo(_s.VehicleYear)))
                foreach (var sg in c.Signals) { var vm = new EnhancedSignalViewModel(c, sg); vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EnhancedSignalViewModel.IsSelected)) OnToggled(vm); }; _all.Add(vm); }
        RebuildModules();
        OnPropertyChanged(nameof(Attribution)); OnPropertyChanged(nameof(HasSet)); OnPropertyChanged(nameof(Hint)); OnPropertyChanged(nameof(SelectedCount));
    }

    partial void OnSelectedModuleChanged(ModuleChoice? value) => BuildSignals();
    partial void OnSearchChanged(string value) => BuildSignals();
    partial void OnIncludeExperimentalChanged(bool value) => BuildSignals();
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsStopped));

    private void RebuildModules()
    {
        var keep = SelectedModule?.Header;
        ModuleChoices.Clear();
        foreach (var hdr in _all.Select(a => a.Command.Header).Distinct().Order())
        {
            var m = FordModules.Find(hdr.ToString("X3"));
            var scanned = _s.Modules.Any(x => x.Module.RequestId == hdr);
            int n = _all.Count(a => a.Command.Header == hdr);
            ModuleChoices.Add(new ModuleChoice(hdr, $"{m?.Abbrev ?? hdr.ToString("X3")} · {hdr:X3}  ({n}){(scanned ? "  ✓" : "")}"));
        }
        SelectedModule = ModuleChoices.FirstOrDefault(c => c.Header == keep) ?? ModuleChoices.FirstOrDefault(c => c.Header == 0x7E0) ?? ModuleChoices.FirstOrDefault();
        BuildSignals();
    }

    private void BuildSignals()
    {
        Signals.Clear();
        if (SelectedModule is null) return;
        var q = Search.Trim();
        foreach (var a in _all.Where(a => a.Command.Header == SelectedModule.Header && (IncludeExperimental || !a.Experimental)
                                         && (q.Length == 0 || a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Group.Contains(q, StringComparison.OrdinalIgnoreCase)))
                                 .OrderBy(a => a.Group).ThenBy(a => a.Name))
            Signals.Add(a);
    }

    private void OnToggled(EnhancedSignalViewModel vm)
    {
        if (vm.IsSelected)
        {
            if (Tiles.Count >= MaxSelected) { vm.IsSelected = false; _s.Info($"At most {MaxSelected} values can be shown at once."); return; }
            Tiles.Add(new EnhancedTile(vm));
        }
        else if (Tiles.FirstOrDefault(t => t.Source == vm) is { } t) Tiles.Remove(t);
        OnPropertyChanged(nameof(SelectedCount));
        StartCommand.NotifyCanExecuteChanged();
    }

    private ModuleSession SessionFor(uint header)
    {
        if (_s.Modules.FirstOrDefault(m => m.Module.RequestId == header) is { } found) return found.Session;
        var m = FordModules.Find(header.ToString("X3")) ?? FordModules.Unknown(header);
        return new ModuleSession(_s.Transport!, m, m.TypicalBus);
    }

    private bool CanStart() => _s.IsConnected && !IsRunning && Tiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        var groups = Tiles.GroupBy(t => t.Source.Command).Select(g => (Cmd: g.Key, Tiles: g.ToList(), Fails: new int[1])).ToList();
        _s.Info($"Module data started ({Tiles.Count} values, {groups.Count} request(s)).");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int cycles = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var g in groups.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    if (g.Fails[0] >= 3) continue;
                    var session = SessionFor(g.Cmd.Header);
                    try
                    {
                        var data = await _s.WithBusAsync(_ => PidReader.ReadAsync(session, g.Cmd, ct).AsTask(), ct);
                        g.Fails[0] = 0;
                        foreach (var t in g.Tiles)
                        {
                            if (data.Length < t.Source.Signal.RequiredBytes) { t.MarkFailed("short"); continue; }
                            t.Update(t.Source.Signal.Format(data), t.Source.Signal.Decode(data));
                        }
                    }
                    catch (Exception ex) when (ex is NegativeResponseException or EcuTimeoutException or ProtocolException)
                    {
                        if (++g.Fails[0] >= 3) foreach (var t in g.Tiles) t.MarkFailed(ex is NegativeResponseException ? "not supported" : "no answer");
                    }
                }
                cycles++;
                Rate = $"{cycles / Math.Max(0.001, sw.Elapsed.TotalSeconds):F1} rounds/s";
                await Task.Delay(30, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { _s.Error("Module data stopped: " + ex.Message); }
        finally { IsRunning = false; _s.Info("Module data stopped."); }
    }

    [RelayCommand]
    public void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void ClearSelection() { foreach (var a in _all.Where(a => a.IsSelected).ToList()) a.IsSelected = false; }
}
