using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.Comms;
using FordDiag.App.Services;
using FordDiag.Core;

namespace FordDiag.App.ViewModels;

public sealed record DtcRow(string Module, string Bus, string Code, string Description, string Status, bool Active);

public sealed partial class DtcViewModel : PageViewModel
{
    private readonly AppState _s;
    private const string AllModules = "All modules";

    public DtcViewModel(AppState state)
    {
        _s = state;
        _scope = AllModules;
        state.Modules.CollectionChanged += (_, _) => RebuildScopes();
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.IsExpertMode)) ClearCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(AppState.State)) { ReadCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(Hint)); }
        };
        Scopes.Add(AllModules);
    }

    public override string Title => "Trouble codes";
    public AppState State => _s;
    public ObservableCollection<DtcRow> Rows { get; } = new();
    public ObservableCollection<string> Scopes { get; } = new();

    [ObservableProperty] private string _scope;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ReadCommand)), NotifyCanExecuteChangedFor(nameof(ClearCommand))] private bool _isBusy;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasRead;

    public bool ShowEmpty => HasRead && Rows.Count == 0;
    public bool ShowHint => !HasRead;
    public string Hint => _s.IsConnected ? (_s.Modules.Count == 0 ? "Scan the vehicle on the Modules page first, then read trouble codes." : "Press Read codes.") : "Connect to a vehicle first.";
    partial void OnHasReadChanged(bool value) { OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(ShowHint)); }

    private void RebuildScopes()
    {
        Scopes.Clear(); Scopes.Add(AllModules);
        foreach (var m in _s.Modules) Scopes.Add(m.Abbrev);
        Scope = AllModules;
        ReadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Hint));
    }

    private IEnumerable<ModuleInfo> Targets() => Scope == AllModules ? _s.Modules : _s.Modules.Where(m => m.Abbrev == Scope);

    private bool CanRead() => _s.IsConnected && _s.Modules.Count > 0 && !IsBusy;
    private bool CanClear() => CanRead() && _s.IsExpertMode;

    [RelayCommand(CanExecute = nameof(CanRead))]
    private async Task ReadAsync()
    {
        IsBusy = true;
        try { await ReadCoreAsync(); }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { _s.Error("DTC read failed: " + ex.Message); Summary = "Read failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task ReadCoreAsync()
    {
        var rows = new List<DtcRow>();
        foreach (var m in Targets().ToList())
        {
            try
            {
                var dtcs = await _s.WithBusAsync(_ => m.Session.ReadDtcsAsync().AsTask());
                m.DtcCount = dtcs.Count;
                foreach (var d in dtcs)
                {
                    var st = new DtcStatus(d.Status);
                    rows.Add(new DtcRow(m.Abbrev, m.BusText, d.Text, DtcDescriptions.Describe(d), st.Summary, st.TestFailed || st.WarningIndicator));
                }
            }
            catch (CommsException ex) { _s.Error($"{m.Abbrev}: {ex.Message}"); }
        }
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        HasRead = true;
        OnPropertyChanged(nameof(ShowEmpty));
        Summary = rows.Count == 0 ? "No trouble codes stored." : $"{rows.Count} trouble code(s) in {rows.Select(r => r.Module).Distinct().Count()} module(s).";
        _s.Info("DTC read: " + Summary);
    }

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearAsync()
    {
        var targets = Targets().ToList();
        if (!await _s.Dialogs.ConfirmAsync("Clear trouble codes?",
                $"This erases stored codes and freeze-frame data in: {string.Join(", ", targets.Select(t => t.Abbrev))}. Readiness monitors reset and the fault may return.", "Clear codes", danger: true))
            return;
        IsBusy = true;
        try
        {
            foreach (var m in targets)
            {
                try { await _s.WithBusAsync(_ => m.Session.ClearDtcsAsync().AsTask()); _s.Tx($"Cleared DTCs in {m.Abbrev}"); }
                catch (CommsException ex) { _s.Error($"{m.Abbrev}: {ex.Message}"); }
            }
            await ReadCoreAsync();
        }
        finally { IsBusy = false; }
    }
}
