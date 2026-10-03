using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FordDiag.App.ViewModels;

namespace FordDiag.App.Views;

public partial class CaptureView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public CaptureView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { if (DataContext is CaptureViewModel { IsRunning: true } vm) vm.Refresh(); };
        AttachedToVisualTree += (_, _) => _timer.Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        KeyDown += (_, e) => { if (e.Key == Key.Enter && DataContext is CaptureViewModel vm && vm.MarkerText.Length > 0) { vm.AddMarkerCommand.Execute(null); e.Handled = true; } };
    }
}
