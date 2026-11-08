using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Backspace.ViewModels;

namespace Backspace.Views;

/// <summary>Mount's revision picker (U162). Returns the ticked revisions, or
/// null when cancelled. Enter mounts the selection from anywhere in the
/// window, including a focused tick box; Escape cancels.</summary>
public partial class MountRevisionPickerWindow : Window
{
    public MountRevisionPickerWindow()
    {
        InitializeComponent();
        // Tunnel: a focused CheckBox would otherwise take Enter as a toggle.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(null); }
            else if (e.Key == Key.Enter) { e.Handled = true; Mount(); }
        }, RoutingStrategies.Tunnel);
    }

    public MountRevisionPickerWindow(MountRevisionPickerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) =>
        {
            await viewModel.LoadAsync();
            MountButton.Focus();
        };
    }

    private void Mount()
    {
        if (DataContext is MountRevisionPickerViewModel vm && vm.ToSelection() is { } selection)
            Close(selection);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
    private void OnMount(object? sender, RoutedEventArgs e) => Mount();
}
