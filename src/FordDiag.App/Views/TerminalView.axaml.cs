using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using FordDiag.App.ViewModels;

namespace FordDiag.App.Views;

public partial class TerminalView : UserControl
{
    public TerminalView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TerminalViewModel vm) vm.LogChanged += () => Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
        };
        InputBox.KeyDown += (_, e) =>
        {
            if (DataContext is not TerminalViewModel vm) return;
            if (e.Key == Key.Enter) { vm.SendCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Up) { vm.HistoryUp(); e.Handled = true; }
            else if (e.Key == Key.Down) { vm.HistoryDown(); e.Handled = true; }
        };
    }
}
