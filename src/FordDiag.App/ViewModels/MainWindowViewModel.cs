using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.App.Services;

namespace FordDiag.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
    /// <summary>Called when the page is navigated away from (stop timers, polling).</summary>
    public virtual void OnLeave() { }
}

public sealed record NavItem(string Title, Func<Geometry> IconFactory, PageViewModel Page)
{
    public Geometry Icon => IconFactory();
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(AppState state)
    {
        State = state;
        var connect = new ConnectViewModel(state);
        var modules = new ModulesViewModel(state);
        var service = new ServiceViewModel(state);
        NavItems = new NavItem[]
        {
            new("Connect", () => Icons.Usb, connect),
            new("Modules", () => Icons.Modules, modules),
            new("Trouble codes", () => Icons.Warning, new DtcViewModel(state)),
            new("Live data", () => Icons.Chart, new LiveDataViewModel(state)),
            new("Service", () => Icons.Wrench, service),
            new("Coding", () => Icons.Edit, new CodingViewModel(state)),
            new("Terminal", () => Icons.Terminal, new TerminalViewModel(state)),
            new("Capture", () => Icons.Record, new CaptureViewModel(state)),
            new("Settings", () => Icons.Settings, new SettingsViewModel(state)),
        };
        _selectedNav = NavItems[0];
        connect.ConnectedCommandHook = () => SelectedNav = NavItems[1];
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.IsExpertMode)) OnPropertyChanged(nameof(ExpertText)); };
    }

    public AppState State { get; }
    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Current))] private NavItem _selectedNav;
    public PageViewModel Current => SelectedNav.Page;

    partial void OnSelectedNavChanging(NavItem value) => SelectedNav.Page.OnLeave();
    partial void OnSelectedNavChanged(NavItem value) { if (value.Page is ServiceViewModel sv) sv.Initialise(); }

    public string ExpertText => State.IsExpertMode ? "Expert mode ON" : "Expert mode off";

    public void Navigate(string title) => SelectedNav = NavItems.First(n => n.Title == title);

    [RelayCommand]
    private async Task ToggleExpertAsync()
    {
        if (State.IsExpertMode) { State.IsExpertMode = false; State.Info("Expert mode disabled."); }
        else if (await State.Dialogs.ConfirmAsync("Enable expert mode?",
                     "Expert mode allows writing to vehicle modules (coding, clearing codes, raw requests). A bad write or a flat battery can permanently damage a module. Only continue if you know what you are doing and the vehicle is on a battery maintainer.",
                     "Enable expert mode", danger: true))
        { State.IsExpertMode = true; State.Info("Expert mode ENABLED."); }
        OnPropertyChanged(nameof(ExpertText));
    }
}
