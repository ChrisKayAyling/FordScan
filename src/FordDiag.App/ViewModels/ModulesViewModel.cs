using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.Comms;
using FordDiag.App.Services;
using FordDiag.Core;

namespace FordDiag.App.ViewModels;

public sealed partial class ModulesViewModel : PageViewModel
{
    private readonly AppState _s;
    private CancellationTokenSource? _cts;

    public ModulesViewModel(AppState state)
    {
        _s = state;
        _fullRange = state.Settings.FullRangeScan;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.State)) { ScanCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ShowHint)); }
            if (e.PropertyName is nameof(AppState.SelectedModule)) OnPropertyChanged(nameof(Selected));
        };
        state.Modules.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(ShowHint)); OnPropertyChanged(nameof(Summary)); };
    }

    public override string Title => "Modules";
    public AppState State => _s;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ScanCommand))] private bool _isScanning;
    [ObservableProperty] private bool _fullRange;
    [ObservableProperty] private string _progress = "";

    partial void OnFullRangeChanged(bool value) => _s.Settings.FullRangeScan = value;

    public ModuleInfo? Selected
    {
        get => _s.SelectedModule;
        set { _s.SelectedModule = value; OnPropertyChanged(); }
    }

    public bool ShowHint => _s.Modules.Count == 0;
    public string Summary
    {
        get
        {
            if (_s.Modules.Count == 0) return "No scan yet";
            int dtcs = _s.Modules.Sum(m => m.DtcCount ?? 0);
            return $"{_s.Modules.Count} modules · {(dtcs == 0 ? "no trouble codes" : dtcs + " trouble codes")}";
        }
    }

    private bool CanScan() => _s.IsConnected && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsScanning = true;
        _s.Modules.Clear(); _s.SelectedModule = null; _s.Vin = null;
        try
        {
            var opts = new ScanOptions
            {
                FullRange = FullRange,
                Switching = _s.Switching,
                Log = new Progress<string>(m => { Progress = m; _s.Info(m); }),
                PromptBusSwitch = async (bus, _) =>
                {
                    if (!await _s.Dialogs.ConfirmAsync("Switch adapter bus",
                            $"Set the adapter's HS/MS switch to {bus.Label()} and press Continue.", "Continue"))
                        throw new OperationCanceledException();
                },
            };
            var found = new List<FoundModule>();
            await _s.WithBusAsync(async t =>
            {
                await foreach (var f in FordScanner.ScanAsync(t, opts, ct))
                {
                    var info = new ModuleInfo(f.Module, f.Bus) { Session = new ModuleSession(t, f.Module, f.Bus) };
                    _s.Modules.Add(info);
                    Progress = $"Reading {f.Module.Abbrev}…";
                    try
                    {
                        info.Identification = (await info.Session.ReadIdentificationAsync(ct)).Select(i => new IdentLine(i.Did, i.Name, i.Value)).ToList();
                        info.DtcCount = (await info.Session.ReadDtcsAsync(0xAF, ct)).Count;
                    }
                    catch (Exception ex) when (ex is CommsException) { _s.Error($"{f.Module.Abbrev}: {ex.Message}"); }
                    _s.Vin ??= info.Identification.FirstOrDefault(i => i.Did == 0xF190)?.Value;
                }
            }, ct);
            Progress = $"Scan complete: {_s.Modules.Count} module(s).";
            _s.Info(Progress);
            _s.SelectedModule = _s.Modules.FirstOrDefault();
            OnPropertyChanged(nameof(Selected)); OnPropertyChanged(nameof(Summary));
        }
        catch (OperationCanceledException) { Progress = "Scan cancelled."; }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { Progress = "Scan failed: " + ex.Message; _s.Error(Progress); }
        finally { IsScanning = false; _cts = null; await _s.RefreshVoltageAsync(); }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private Task LookupVehicleAsync() => _s.LookupVehicleAsync();
}
