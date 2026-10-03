using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.App.Services;
using FordDiag.Comms;
using FordDiag.Core;
using FordDiag.Core.Service;

namespace FordDiag.App.ViewModels;

/// <summary>Guided service functions and output tests.</summary>
public sealed partial class ServiceViewModel : PageViewModel
{
    private readonly AppState _s;
    private IReadOnlyList<Procedure>? _library;
    private CancellationTokenSource? _cts;

    public ServiceViewModel(AppState state)
    {
        _s = state;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.State) or nameof(AppState.IsExpertMode)) NotifyCommands();
        };
        state.Modules.CollectionChanged += (_, _) => { if (SelectedTarget is null) SelectedTarget = state.Modules.FirstOrDefault(); BuildList(); };
    }

    public override string Title => "Service";
    public AppState State => _s;
    public IReadOnlyList<Procedure> Library => _library ??= ProcedureLibrary.Load(Path.Combine(AppSettings.ConfigDir, "service"), w => _s.Error("Procedure file skipped: " + w));
    public ObservableCollection<Procedure> Items { get; } = new();
    public ObservableCollection<ModuleInfo> Targets => _s.Modules;
    public ObservableCollection<string> Output { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowTests))] private bool _showServices = true;
    [ObservableProperty] private Procedure? _selected;
    [ObservableProperty] private ModuleInfo? _selectedTarget;
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _resultOk;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isRunning;

    public bool ShowTests => !ShowServices;
    public bool IsIdle => !IsRunning;
    public bool HasSelected => Selected is not null;
    public bool NeedsTarget => Selected is { Module: null };
    public bool ExpertRequired => !_s.IsExpertMode;
    public bool HasConditions => Selected?.Conditions.Count > 0;
    public bool HasWarnings => Selected?.Warnings.Count > 0;
    public bool HasManual => Selected?.Manual.Count > 0;
    public string Badge => Selected?.Confidence switch { "standard" => "Standard (ISO 14229 / SAE J1979)", "user" => "Your definition", "captured" => "Captured from a real run, verify before use", _ => "Community-reported, not verified" };
    public string SourceText => Selected?.Source ?? "";
    public string TargetText => Selected is null ? "" : TargetModule()?.Abbrev is { } a ? $"Runs on {a} ({TargetModule()!.RequestId:X3})" : "Choose the module";
    public IEnumerable<string> StepLines => Selected is null ? Enumerable.Empty<string>()
        : Selected.Steps.Select((st, i) => $"{i + 1}.  {st.Hex,-14} {st.Description}" + (st.HoldSeconds > 0 ? $"  (hold {st.HoldSeconds:0.#} s)" : ""))
            .Concat(Selected.Cleanup.Select(st => $"always  {st.Hex,-12} {st.Description}"));

    partial void OnShowServicesChanged(bool value) => BuildList();
    partial void OnSelectedChanged(Procedure? value)
    {
        Output.Clear(); Result = "";
        foreach (var n in new[] { nameof(HasSelected), nameof(NeedsTarget), nameof(HasConditions), nameof(HasWarnings), nameof(HasManual), nameof(Badge), nameof(SourceText), nameof(TargetText), nameof(StepLines) })
            OnPropertyChanged(n);
        NotifyCommands();
    }
    partial void OnSelectedTargetChanged(ModuleInfo? value) { OnPropertyChanged(nameof(TargetText)); NotifyCommands(); }
    partial void OnIsRunningChanged(bool value) => NotifyCommands();

    [RelayCommand] private void ShowServicesTab() => ShowServices = true;
    [RelayCommand] private void ShowTestsTab() => ShowServices = false;

    public void Initialise() { if (Items.Count == 0) BuildList(); }

    private void BuildList()
    {
        var keep = Selected?.Id;
        Items.Clear();
        foreach (var p in Library.Where(p => (p.Kind == ProcedureKind.Service) == ShowServices)
                                  .OrderBy(p => p.Category).ThenBy(p => p.Name))
            Items.Add(p);
        Selected = Items.FirstOrDefault(p => p.Id == keep) ?? Items.FirstOrDefault();
    }

    private FordModule? TargetModule() => Selected?.Module is uint m ? FordModules.Find(m.ToString("X3")) : SelectedTarget?.Module;

    private ModuleSession? SessionForTarget()
    {
        if (_s.Transport is null) return null;
        if (Selected?.Module is uint m)
        {
            if (_s.Modules.FirstOrDefault(x => x.Module.RequestId == m) is { } found) return found.Session;
            var mod = FordModules.Find(m.ToString("X3")) ?? FordModules.Unknown(m);
            return new ModuleSession(_s.Transport, mod, mod.TypicalBus);
        }
        return SelectedTarget?.Session;
    }

    private void NotifyCommands() { PreviewCommand.NotifyCanExecuteChanged(); RunCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ExpertRequired)); }

    private bool CanPreview() => _s.IsConnected && Selected is not null && SessionForTarget() is not null && !IsRunning;
    private bool CanRun() => CanPreview() && _s.IsExpertMode;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task PreviewAsync() => await ExecuteAsync(commit: false);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var p = Selected!;
        var lines = string.Join("\n", p.Warnings.Select(w => "• " + w).Concat(p.Conditions.Select(c => "☐ " + c)));
        if (!await _s.Dialogs.ConfirmAsync($"{(p.Kind == ProcedureKind.OutputTest ? "Run test" : "Run")} \"{p.Name}\"?",
                $"This sends commands to {TargetModule()?.Abbrev}. Make sure the conditions below are met.\n\n{lines}", p.Kind == ProcedureKind.OutputTest ? "Start test" : "Run", danger: true))
            return;
        await ExecuteAsync(commit: true);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private async Task ExecuteAsync(bool commit)
    {
        var p = Selected!; var session = SessionForTarget()!;
        Output.Clear(); Result = ""; IsRunning = true; _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(m => Output.Add("→ " + m));
            var res = await _s.WithBusAsync(_ => ProcedureRunner.RunAsync(session, p, new ProcedureOptions
            { Commit = commit, ReadVoltage = _s.ReadVoltageUnlockedAsync, Progress = progress }, _cts.Token), CancellationToken.None);
            foreach (var st in res.Steps) Output.Add($"{(st.Ok ? "✓" : "✗")} {st.Hex,-14} {(string.IsNullOrEmpty(st.Response) ? "" : "→ " + st.Response)}");
            ResultOk = res.Success; Result = res.Message;
            if (commit) { _s.Tx($"{p.Name}: {res.Message}"); if (!res.Success) _s.Error($"{p.Name}: {res.Message}"); }
        }
        catch (Exception ex) when (ex is CommsException or InvalidOperationException) { ResultOk = false; Result = ex.Message; _s.Error(ex.Message); }
        finally { IsRunning = false; _cts = null; }
    }
}
