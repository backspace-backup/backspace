using Avalonia.Controls;
using Avalonia.Interactivity;
using Backspace.ViewModels;

namespace Backspace.Views;

public partial class RunChangesWindow : Window
{
    public RunChangesWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Avalonia.Input.Key.Back && DataContext is RunChangesViewModel vm) { e.Handled = true; vm.UpCommand.Execute(null); }
        };
        var map = this.FindControl<ChangeTreemap>("Map")!;
        map.Opened += node => (DataContext as RunChangesViewModel)?.Open(node);
        map.Hovered += node => (DataContext as RunChangesViewModel)?.Hover(node);
        Closed += (_, _) => (DataContext as RunChangesViewModel)?.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
