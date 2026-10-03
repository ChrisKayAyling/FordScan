using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace FordDiag.App.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false);
    Task<string?> PickOpenFileAsync(string title, string filterName, params string[] patterns);
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string filterName, params string[] patterns);
}

/// <summary>Real dialogs over an Avalonia window (message boxes are built in code so no extra view is needed).</summary>
public sealed class WindowDialogs : IDialogService
{
    private readonly Window _owner;
    public WindowDialogs(Window owner) => _owner = owner;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false)
    {
        var dlg = new Window
        {
            Title = title, Width = 460, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        bool result = false;
        var ok = new Button { Content = confirmText, Classes = { danger ? "danger" : "accent" } };
        var cancel = new Button { Content = "Cancel", Classes = { "plain" } };
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 18,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        await dlg.ShowDialog(_owner);
        return result;
    }

    public async Task<string?> PickOpenFileAsync(string title, string filterName, params string[] patterns)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        { Title = title, AllowMultiple = false, FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns } } });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string filterName, params string[] patterns)
    {
        var f = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        { Title = title, SuggestedFileName = suggestedName, FileTypeChoices = new[] { new FilePickerFileType(filterName) { Patterns = patterns } } });
        return f?.TryGetLocalPath();
    }
}
