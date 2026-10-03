using Avalonia;
using Avalonia.Styling;

namespace FordDiag.App.Services;

public static class ThemeService
{
    public static readonly string[] Choices = { "System", "Light", "Dark" };

    public static void Apply(string theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }
}
