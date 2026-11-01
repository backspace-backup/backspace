using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Backspace.Views;

public partial class DestructiveConfirmWindow : Window
{
    private readonly string _confirm;
    private readonly StringComparison _cmp;
    private readonly bool _informational;
    private bool _closed;

    public DestructiveConfirmWindow() : this("Confirm", "", "", caseInsensitive: false) { }

    public DestructiveConfirmWindow(string title, string subtitle, string confirmString, bool caseInsensitive = false)
    {
        InitializeComponent();
        _confirm = confirmString ?? "";
        _cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Informational mode = no typing required. Triggered when the
        // caller passes an empty confirmString (used by code paths that
        // want to surface a "can't do that, here's why" message — e.g.
        // "destination is in use by a running backup"). Showing the
        // type-OK prompt for those was awful UX: the user had to acknowledge
        // a non-action by typing into a confirm box.
        _informational = string.IsNullOrEmpty(_confirm);

        TitleBlock.Text = title;
        SubtitleBlock.Text = subtitle;

        if (_informational)
        {
            ConfirmRule.IsVisible = false;
            ConfirmInputBlock.IsVisible = false;
            // Reframe the buttons: only "OK", styled as a ghost button
            // (nothing destructive happens). Hide the Cancel button —
            // the dialog is purely informational and there's no
            // alternative action to take.
            CancelButton.IsVisible = false;
            ProceedButton.Content = "OK";
            ProceedButton.Classes.Remove("danger");
            ProceedButton.Classes.Add("primary");
        }
        else
        {
            PromptLabel.Text = $"TYPE “{confirmString}” TO CONFIRM";
        }

        // Focus the right control on open: input box in confirm mode,
        // proceed button in info mode. Hooking Opened (rather than the
        // ctor) ensures the visual tree is realized.
        Opened += (_, _) =>
        {
            if (_informational) ProceedButton.Focus();
            else ConfirmInput.Focus();
        };

        // Keyboard shortcuts. Escape always cancels/closes (matches OS-wide
        // dialog convention). Enter submits — same path as clicking the
        // primary button.
        KeyDown += OnKeyDown;

        // Mark the window as closed so the red-flash timer's dispatcher
        // post can no-op instead of touching a defunct visual tree.
        Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// Convenience factory for informational "you can't do that right
    /// now" dialogs. No type-OK gate, just a single OK button. Returns
    /// a Task<bool> for symmetry with the destructive variant; the
    /// bool is always true (user acknowledged) or false (Escape).
    /// </summary>
    public static System.Threading.Tasks.Task<bool> ShowInfoAsync(
        Window owner, string title, string subtitle)
    {
        var dlg = new DestructiveConfirmWindow(title, subtitle, confirmString: "", caseInsensitive: true);
        return dlg.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// Two-button confirm dialog with no type-OK gate. Used by the
    /// close-X interceptor when the user tries to quit while
    /// backups are running — typing "Quit" would be UX punishment
    /// for a non-destructive action. Returns true if the user
    /// clicks the confirm button, false if they cancel / press
    /// Escape / hit the window's X.
    /// </summary>
    public static System.Threading.Tasks.Task<bool> ShowConfirmAsync(
        Window owner, string title, string subtitle,
        string confirmLabel = "OK", string cancelLabel = "Cancel")
    {
        var dlg = new DestructiveConfirmWindow(title, subtitle, confirmString: "", caseInsensitive: true);
        // Override the info-mode button setup: re-show Cancel, restore
        // the primary "Proceed" button with the caller's label, and
        // keep the no-typing behaviour. Done after construction so the
        // existing info-mode wiring stays intact for the single-button
        // callers.
        dlg.CancelButton.IsVisible = true;
        dlg.CancelButton.Content = cancelLabel;
        dlg.ProceedButton.Content = confirmLabel;
        // Confirm button stays styled as "primary" (not "danger") —
        // this is a "are you sure?" prompt, not an erase confirmation.
        dlg.ProceedButton.Classes.Remove("danger");
        dlg.ProceedButton.Classes.Add("primary");
        return dlg.ShowDialog<bool>(owner);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OnProceed(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnProceed(object? sender, RoutedEventArgs e)
    {
        if (_informational)
        {
            // No gate to clear — single-button acknowledgement.
            Close(true);
            return;
        }

        if (string.Equals(ConfirmInput.Text?.Trim(), _confirm, _cmp))
        {
            Close(true);
        }
        else
        {
            // Visual cue: tint input red briefly then reset. The
            // _closed guard prevents the dispatcher post from touching
            // ConfirmInput after the user dismissed the window mid-flash
            // (Esc, ✕). Without it Avalonia logs first-chance exceptions
            // in debugger sessions and risks a use-after-close on
            // future Avalonia releases.
            ConfirmInput.Foreground = Brushes.Red;
            ConfirmInput.Focus();
            // DispatcherTimer — bound to the dispatcher, so when the
            // user dismisses the window the timer is collected along
            // with the dispatcher's reference graph. The prior
            // System.Threading.Timer kept the window rooted via its
            // captured closure until the 500 ms tick fired, even after
            // Close.
            Avalonia.Threading.DispatcherTimer? dt = null;
            dt = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            dt.Tick += (_, __) =>
            {
                dt!.Stop();
                if (_closed) return;
                try { ConfirmInput.ClearValue(TextBox.ForegroundProperty); } catch { }
            };
            dt.Start();
        }
    }
}
