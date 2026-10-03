using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FordDiag.App.Services;
using FordDiag.Core;

namespace FordDiag.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppState _s;
    public SettingsViewModel(AppState state)
    {
        _s = state;
        _theme = state.Settings.Theme;
        _timeout = state.Settings.ResponseTimeoutMs;
    }

    public override string Title => "Settings";
    public IReadOnlyList<string> Themes => ThemeService.Choices;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public string ConfigDir => AppSettings.ConfigDir;
    public string DefinitionsDir => System.IO.Path.Combine(AppSettings.ConfigDir, "definitions");
    public string PidsDir => System.IO.Path.Combine(AppSettings.ConfigDir, "pids");
    public string ServiceDir => System.IO.Path.Combine(AppSettings.ConfigDir, "service");
    public string BackupDir => new BackupStore().Directory;

    [ObservableProperty] private string _theme;
    [ObservableProperty] private decimal _timeout;

    partial void OnThemeChanged(string value) { _s.Settings.Theme = value; ThemeService.Apply(value); _s.Settings.Save(); }
    partial void OnTimeoutChanged(decimal value) { _s.Settings.ResponseTimeoutMs = (int)value; _s.Settings.Save(); }
}
