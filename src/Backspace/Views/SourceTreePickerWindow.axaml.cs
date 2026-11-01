using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Backspace.ViewModels;

namespace Backspace.Views;

public partial class SourceTreePickerWindow : Window
{
    private readonly SourceTreePickerViewModel _vm;

    public SourceTreePickerWindow() : this(System.Array.Empty<string>()) { }

    public SourceTreePickerWindow(IEnumerable<string> initiallyChecked)
    {
        _vm = new SourceTreePickerViewModel(initiallyChecked);
        DataContext = _vm;
        InitializeComponent();
        WindowPlacement.Attach(this);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        _vm.ConfirmCommand.Execute(null);
        Close(_vm.Result);
    }

    /// <summary>
    /// Lenient hit-target for tree rows: clicking ANY non-checkbox
    /// part of the row toggles expand/collapse on the bound
    /// <see cref="FileSystemNode"/>. Mirrors Explorer's row behaviour
    /// and saves the user from chasing the small chevron.
    /// </summary>
    private void OnTreeRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control row || row.DataContext is not FileSystemNode node)
            return;

        // Left-click only — right / middle clicks should keep their
        // platform-native context-menu / new-tab semantics. Found by
        // adversarial self-review per AGENT.md rule 7 before the
        // user reported a right-click-also-expands surprise.
        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;

        // Click on the checkbox? Leave selection semantics alone.
        if (e.Source is Visual src)
        {
            foreach (var ancestor in src.GetSelfAndVisualAncestors())
            {
                if (ancestor is CheckBox) return;
            }
        }

        // Placeholder rows have no children to expand.
        if (node.IsPlaceholder) return;

        node.IsExpanded = !node.IsExpanded;
        e.Handled = true;
    }
}
