using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.Comms.Serial;
using FordDiag.App.Services;
using FordDiag.Core;

namespace FordDiag.App.ViewModels;

public sealed partial class ConnectViewModel : PageViewModel
{
    private readonly AppState _s;

    public ConnectViewModel(AppState state)
    {
        _s = state;
        _adapter = FordAdapters.Find(state.Settings.AdapterKey) ?? FordAdapters.All[0];
        _port = state.Settings.Port;
        _s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.State)) { OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanDisconnect)); OnPropertyChanged(nameof(ConnectedHint)); }
            if (e.PropertyName is nameof(AppState.Voltage)) OnPropertyChanged(nameof(VoltageHint));
        };
        RefreshPorts();
    }

    public override string Title => "Connect";
    public AppState State => _s;
    public IReadOnlyList<FordAdapter> Adapters => FordAdapters.All;
    public ObservableCollection<string> Ports { get; } = new();
    public Action? ConnectedCommandHook { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SwitchingText)), NotifyPropertyChangedFor(nameof(AdapterNotes))] private FordAdapter _adapter;
    [ObservableProperty] private string _port;
    [ObservableProperty] private bool _useSimulator;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public string AdapterNotes => Adapter.Notes;
    public bool CanConnect => _s.State == ConnectionState.Disconnected && !IsBusy;
    public bool CanDisconnect => _s.State == ConnectionState.Connected;
    public string ConnectedHint => _s.IsConnected ? "Adapter ready. Open Modules to scan the vehicle." : "Plug the adapter into the OBD-II port and switch the ignition on (engine off).";
    public string VoltageHint => _s.Voltage is double v ? (v < 12.0 ? "Battery is low: connect a charger before any coding." : "Battery voltage is fine for diagnostics.") : "";
    public string SwitchingText => Adapter.Switching switch
    {
        BusSwitching.Automatic => "HS-CAN and MS-CAN are switched automatically by the adapter.",
        BusSwitching.Manual => "HS-CAN only until you flip the adapter's HS/MS switch; you are prompted during a scan.",
        _ => "HS-CAN only. MS-CAN modules (body, cluster, audio) are not reachable.",
    };

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (var p in PortEnumerator.GetPorts()) Ports.Add(p.Name);
        if (string.IsNullOrEmpty(Port) && Ports.Count > 0) Port = Ports[0];
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Error = null; IsBusy = true;
        try
        {
            await _s.ConnectAsync(Adapter, Port, UseSimulator);
            _s.Settings.AdapterKey = Adapter.Key; _s.Settings.Port = Port ?? "";
            ConnectedCommandHook?.Invoke();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectAsync() => await _s.DisconnectAsync();

    [RelayCommand]
    private Task RefreshVoltageAsync() => _s.RefreshVoltageAsync();
}
