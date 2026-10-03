using Avalonia;
using FordDiag.App.Services;

namespace FordDiag.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `FordDiag --screenshots <dir>`: render every page headlessly (no display needed) for docs/visual review.
        var i = Array.IndexOf(args, "--screenshots");
        if (i >= 0) return ScreenshotRunner.Run(i + 1 < args.Length ? args[i + 1] : "screenshots");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
