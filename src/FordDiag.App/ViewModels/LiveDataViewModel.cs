using System.Collections.ObjectModel;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.Comms;
using FordDiag.App.Services;
using FordDiag.Core;

namespace FordDiag.App.ViewModels;

public sealed partial class LiveTile : ObservableObject
{
    private const int History = 60;
    private readonly List<double> _samples = new();

    public LiveTile(ObdPid pid) { Pid = pid; }
    public ObdPid Pid { get; }
    public string Name => Pid.Name;
    public string Unit => Pid.Unit;

    [ObservableProperty] private string _valueText = "–";
    [ObservableProperty] private bool _available = true;
    [ObservableProperty] private IList<Point> _points = new List<Point> { new(0, 30), new(120, 30) };

    public void Push(double value)
    {
        _samples.Add(value);
        if (_samples.Count > History) _samples.RemoveAt(0);
        ValueText = value.ToString(Pid.Format, System.Globalization.CultureInfo.InvariantCulture);
        double span = Math.Max(1e-9, Pid.Max - Pid.Min);
        Points = _samples.Select((v, i) => new Point(i * 190.0 / (History - 1), 34 - Math.Clamp((v - Pid.Min) / span, 0, 1) * 32 - 1)).ToList();
    }

    public void MarkUnavailable() { Available = false; ValueText = "n/a"; }
}

/// <summary>Polls SAE J1979 mode 01 PIDs from the PCM at a few Hz.</summary>
public sealed partial class LiveDataViewModel : PageViewModel
{
    private readonly AppState _s;
    private CancellationTokenSource? _cts;

    public LiveDataViewModel(AppState state)
    {
        _s = state;
        foreach (var p in ObdPids.All) Tiles.Add(new LiveTile(p));
        Enhanced = new EnhancedViewModel(state);
        state.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(AppState.State)) { if (!_s.IsConnected) Stop(); StartCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(Hint)); } };
    }

    public override string Title => "Live data";
    public ObservableCollection<LiveTile> Tiles { get; } = new();
    public EnhancedViewModel Enhanced { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsStandard))] private bool _isEnhanced;
    public bool IsStandard => !IsEnhanced;
    partial void OnIsEnhancedChanged(bool value) { Stop(); Enhanced.Stop(); if (value) Enhanced.Initialise(); }

    [RelayCommand] private void ShowStandard() => IsEnhanced = false;
    [RelayCommand] private void ShowEnhanced() => IsEnhanced = true;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand)), NotifyPropertyChangedFor(nameof(IsStopped))] private bool _isRunning;
    [ObservableProperty] private string _rate = "";
    public bool IsStopped => !IsRunning;
    public string Hint => _s.IsConnected ? "Press Start to stream engine data from the PCM (mode 01)." : "Connect to a vehicle first.";

    public override void OnLeave() { Stop(); Enhanced.Stop(); }

    private bool CanStart() => _s.IsConnected && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        var pcm = _s.Modules.FirstOrDefault(m => m.Module.RequestId == 0x7E0)?.Session
                  ?? new ModuleSession(_s.Transport!, FordModules.Find("PCM")!, FordBus.HsCan);
        _s.Info("Live data started (PCM, mode 01).");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int cycles = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var tile in Tiles.Where(t => t.Available))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var rsp = await _s.WithBusAsync(_ => pcm.RequestAsync(new byte[] { 0x01, tile.Pid.Pid }, ct).AsTask(), ct);
                        if (ObdPids.TryDecode(tile.Pid, rsp) is double v) tile.Push(v); else tile.MarkUnavailable();
                    }
                    catch (EcuTimeoutException) { tile.MarkUnavailable(); }
                }
                cycles++;
                Rate = $"{cycles / Math.Max(0.001, sw.Elapsed.TotalSeconds):F1} full updates/s";
                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { _s.Error("Live data stopped: " + ex.Message); }
        finally { IsRunning = false; _s.Info("Live data stopped."); }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();
}
