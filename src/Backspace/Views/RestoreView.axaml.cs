using Avalonia.Controls;
using Backspace.ViewModels;

namespace Backspace.Views;

public partial class RestoreView : UserControl
{
    public RestoreView()
    {
        InitializeComponent();

        // The restore-target radios bind One-Way and write to the VM
        // only through these check-only handlers. Two-way bindings on
        // same-group radios ping-ponged forever under Avalonia's
        // RadioButtonGroupManager while templates applied (each uncheck
        // wrote back to the source, which re-flipped the sibling): the
        // layout pass never converged and the suite hung at 100% CPU.
        // This graph cannot cycle: bindings never write the source, the
        // handlers ignore unchecks, and the VM setter no-ops on equal
        // values.
        TargetOriginalRadio.IsCheckedChanged += (_, _) =>
        {
            if (TargetOriginalRadio.IsChecked == true &&
                DataContext is RestoreViewModel vm)
                vm.TargetOriginalSource = true;
        };
        TargetDifferentRadio.IsCheckedChanged += (_, _) =>
        {
            if (TargetDifferentRadio.IsChecked == true &&
                DataContext is RestoreViewModel vm)
                vm.TargetOriginalSource = false;
        };
    }
}
