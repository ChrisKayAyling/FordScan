using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.Comms;
using FordDiag.Comms.Elm;
using FordDiag.App.Services;
using FordDiag.Core;

namespace FordDiag.App.ViewModels;

public sealed partial class TerminalViewModel : PageViewModel
{
    private readonly AppState _s;
    private readonly List<string> _history = new();
    private int _historyPos;
    // Read-only services; everything else needs expert mode (2E write, 27 security, 31 routines, 11 reset, 14 clear, 34-37 transfer ...).
    private static readonly HashSet<byte> SafeServices = new() { 0x01, 0x02, 0x03, 0x09, 0x10, 0x19, 0x22, 0x3E };

    public TerminalViewModel(AppState state)
    {
        _s = state;
        state.Log.CollectionChanged += (_, _) => OnLogChanged();
        state.Info("Terminal ready. Type AT/ST commands, or hex bytes to send to the selected module (e.g. 22 F1 90).");
    }

    public override string Title => "Terminal";
    public AppState State => _s;
    public IEnumerable<LogLine> Lines => _s.Log.Where(l => ShowTrace || l.Kind != LogKind.Trace);
    public event Action? LogChanged;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Lines))] private bool _showTrace;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private ModuleInfo? _target;
    public System.Collections.ObjectModel.ObservableCollection<ModuleInfo> Modules => _s.Modules;

    private void OnLogChanged() { OnPropertyChanged(nameof(Lines)); LogChanged?.Invoke(); }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0) return;
        _history.Add(text); _historyPos = _history.Count;
        Input = "";
        try
        {
            if (!_s.IsConnected) { _s.Error("Not connected."); return; }
            _s.Tx(text);
            var up = text.ToUpperInvariant();
            if (up.StartsWith("AT") || up.StartsWith("ST"))
            {
                if (!_s.IsExpertMode && !IsHarmlessAdapterCommand(up)) { _s.Error("Adapter configuration commands need expert mode (they can break the session)."); return; }
                var reply = await _s.WithBusAsync(t => ((ElmTransport)t).SendAtAsync(up).AsTask());
                _s.Rx(reply.ToString() ?? "");
                return;
            }
            var m = Target ?? _s.SelectedModule;
            if (m is null) { _s.Error("Select a target module (scan first)."); return; }
            if (!Hex.TryParse(text.Replace(" ", ""), out var req) || req.Length == 0) { _s.Error("Not a hex request."); return; }
            if (!SafeServices.Contains(req[0]) && !_s.IsExpertMode) { _s.Error($"Service 0x{req[0]:X2} can change the vehicle: enable expert mode first."); return; }
            var rsp = await _s.WithBusAsync(_ => m.Session.RequestAsync(req).AsTask());
            string desc = rsp.Length >= 3 && rsp[0] == 0x7F ? $"  ({NegativeResponses.Describe(rsp[2])})" : "";
            _s.Rx($"{m.Abbrev}: {Hex.ToString(rsp, true)}{desc}");
        }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { _s.Error(ex.Message); }
    }

    private static bool IsHarmlessAdapterCommand(string c) => c is "ATI" or "ATZ" or "ATRV" or "ATDP" or "ATDPN" or "STI" or "STDI" or "STPRS" or "AT@1" or "AT@2";

    [RelayCommand] private void Clear() { _s.Log.Clear(); OnLogChanged(); }

    public void HistoryUp() { if (_historyPos > 0) Input = _history[--_historyPos]; }
    public void HistoryDown() { if (_historyPos < _history.Count - 1) Input = _history[++_historyPos]; else { _historyPos = _history.Count; Input = ""; } }
}
