using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FordDiag.App.Services;
using FordDiag.App.ViewModels;
using FordDiag.App.Views;

namespace FordDiag.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            var window = new MainWindow();
            var state = new AppState(new WindowDialogs(window), settings);
            window.DataContext = new MainWindowViewModel(state);
            desktop.MainWindow = window;
            ThemeService.Apply(settings.Theme);
            desktop.Exit += (_, _) => { settings.Save(); state.DisconnectAsync().GetAwaiter().GetResult(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
