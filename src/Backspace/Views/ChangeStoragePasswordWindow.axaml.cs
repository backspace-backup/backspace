using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services;

namespace Backspace.Views;

/// <summary>
/// Dialog that runs <c>restic key passwd</c> against an existing
/// storage to rotate the master-key encryption password. The actual
/// re-encryption is fast (touches only a ~1 KB config file at the
/// storage root, not the chunks), so the UI just shows a brief
/// "Working…" state and surfaces success or the restic error
/// verbatim on failure.
/// <para>
/// Returned dialog result:
///   <c>true</c>  — password changed successfully and the engine saved its new
///                  secret under the exclusive maintenance lease.
///   <c>false</c> — user cancelled or the change failed.
/// </para>
/// The caller can read <see cref="NewPassword"/> to refresh its editor.
/// Closing during work requests cancellation but waits for safe completion.
/// </summary>
public partial class ChangeStoragePasswordWindow : Window
{
    private readonly Destination _destination;
    private readonly string _storageName;
    private readonly string _oldPassword;
    private static Serilog.ILogger _log => AppLogger.For<ChangeStoragePasswordWindow>();

    /// <summary>The new password the user entered. Caller reads this
    /// AFTER ShowDialog returns true to update its secrets store.
    /// Cleared when the dialog opens or on cancel/failure.</summary>
    public string? NewPassword { get; private set; }

    /// <summary>Designer / parameterless ctor — never used at runtime.</summary>
    public ChangeStoragePasswordWindow()
        : this(new Destination { Name = "(designer)" }, "default", "") { }

    public ChangeStoragePasswordWindow(Destination destination, string storageName, string oldPassword)
    {
        _destination = destination;
        _storageName = storageName ?? "default";
        _oldPassword = oldPassword ?? "";

        InitializeComponent();
        TitleBlock.Text = $"Change password for «{destination.Name}»";

        Opened += (_, _) => NewPasswordBox.Focus();
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true;
            RequestCancellation();
        };

        // Escape cancels.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && CancelButton.IsEnabled)
            {
                Close(false);
                e.Handled = true;
            }
        };

        // Belt-and-suspenders: any close path (X button, alt-F4,
        // owner closing) also cancels in-flight work. Without this, a
        // close-via-X would orphan the the engine binary rewrite (OnCancel
        // is only fired by the Cancel button).
        Closed += (_, _) =>
        {
            // Set _closing BEFORE Cancel so the post-cancel resume path
            // in OnConfirm can short-circuit ClearBusy() — mutating
            // Avalonia controls after Closed has run can produce
            // first-chance exceptions that the dispatcher tolerates
            // but show up in debugger sessions.
            _closing = true;
            // OnConfirm captures `localCts` in a stack local before the
            // first await, so the awaiter holds its own reference. We
            // can safely Cancel here; Dispose runs once the awaiter
            // unwinds. To avoid a leak in the success-then-close path
            // (no awaiter to observe), Dispose here too — the awaiter's
            // local reference keeps the CTS alive until use, and a
            // double-Dispose is benign.
            try { _busyCts?.Cancel(); } catch { }
            try { _busyCts?.Dispose(); } catch { }
            _busyCts = null;
        };
    }

    /// <summary>Live cancellation token for the in-flight re-encrypt.
    /// Cancellation stops preparation before mutation. Once key rotation
    /// starts, the engine completes it and saves the new credential before
    /// the dialog may close.</summary>
    private System.Threading.CancellationTokenSource? _busyCts;

    /// <summary>True once the window has been closed. OnConfirm's
    /// resume path checks this before touching UI controls — without
    /// the gate, an OperationCanceledException after Close would run
    /// ClearBusy() on a defunct visual tree.</summary>
    private bool _closing;
    private bool _busy;

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_busy) { RequestCancellation(); return; }
        Close(false);
    }

    private void RequestCancellation()
    {
        try { _busyCts?.Cancel(); } catch { }
        CancelButton.IsEnabled = false;
        StatusInfo.Text = "Stopping safely. If the password change has started, it will finish and save the new password first.";
    }

    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        // Disable Confirm IMMEDIATELY so a rapid double-click can't run
        // OnConfirm twice. Re-enable on each validation-failing return
        // so the user can fix and try again.
        if (!ConfirmButton.IsEnabled) return;
        ConfirmButton.IsEnabled = false;
        ClearError();

        var newPwd  = NewPasswordBox.Text ?? "";
        var confirm = ConfirmPasswordBox.Text ?? "";

        // Client-side validation runs BEFORE the CTS allocation. The
        // previous version Cancel()+Dispose()-ed any prior `_busyCts`
        // here unconditionally — but `_busyCts` could legitimately
        // refer to a STILL-IN-FLIGHT prior attempt (the user clicked
        // Confirm while a rotate from a previous click was running),
        // and that attempt's awaiter would suddenly see
        // ObjectDisposedException instead of OperationCanceledException.
        // Validate first; only allocate when we're committed to running.
        if (newPwd.Length < 8)
        {
            ShowError("Use at least 8 characters for the new password.");
            NewPasswordBox.Focus();
            ConfirmButton.IsEnabled = true;
            return;
        }
        if (newPwd != confirm)
        {
            ShowError("The two passwords don't match. Re-type the confirmation to make sure.");
            ConfirmPasswordBox.Focus();
            ConfirmButton.IsEnabled = true;
            return;
        }
        if (newPwd == _oldPassword)
        {
            ShowError("That's the same as the current password. Pick a different one to actually rotate.");
            NewPasswordBox.Focus();
            ConfirmButton.IsEnabled = true;
            return;
        }
        if (string.IsNullOrEmpty(_oldPassword))
        {
            ShowError("Backspace couldn't read the existing password from this machine's secret store. " +
                      "Without it we can't decrypt the storage's master key. Try removing this destination and creating it afresh.");
            ConfirmButton.IsEnabled = true;
            return;
        }

        // Validation passed — NOW allocate the CTS. Fresh CTS per
        // attempt; OnCancel cancels this; Closed handler also cancels.
        // The OLD _busyCts (if any) is from a prior run that already
        // completed (otherwise ConfirmButton would still be disabled
        // from that run); safe to dispose it now.
        _busyCts?.Dispose();
        _busyCts = new System.Threading.CancellationTokenSource();
        // Snapshot the token + the CTS reference into locals so the
        // post-await resume path doesn't dereference a `_busyCts` that
        // the Closed handler already nulled. The CTS itself isn't
        // disposed by Closed (only Cancel'd) so the local CTS reference
        // stays valid for the awaiter.
        var localCts = _busyCts;
        var ct = localCts.Token;

        SetBusy("Connecting to the destination and re-encrypting the master key…");
        try
        {
            var (exit, output) = await ServiceLocator.Engine.ChangeStoragePasswordAsync(
                _destination, _storageName, _oldPassword, newPwd, ct);
            if (exit == 0)
            {
                NewPassword = newPwd;
                _log.Information(
                    "Storage password rotated for destination {Name} ({Kind}); newLength={Len}",
                    _destination.Name, _destination.Kind, newPwd.Length);
                _busy = false;
                Close(true);
                return;
            }

            // Failure: surface restic's stdout — it almost always
            // contains a clear-enough explanation
            // ("The password is incorrect", "Storage passwords do not
            // match", "Failed to download the storage configuration").
            // Trim to the last few lines so the dialog stays compact.
            var tail = TailLines(output, maxLines: 6);
            ShowError(
                $"Backspace could not change the password (error code {exit}). " +
                $"Most common cause: the saved old password no longer matches what's on the repository. " +
                $"Output:\n{tail}");
            _log.Warning(
                "Storage password change failed: dest={Name} exit={Exit} tail={Tail}",
                _destination.Name, exit, tail);
        }
        catch (OperationCanceledException)
        {
            // Preparation was cancelled before key mutation. It is now safe
            // to close the dialog.
            _log.Information("Storage password change cancelled by user: dest={Name}", _destination.Name);
            _busy = false;
            Close(false);
        }
        catch (Exception ex)
        {
            ShowError("Unexpected error: " + ex.Message);
            _log.Error(ex, "Storage password change threw: dest={Name}", _destination.Name);
        }
        finally
        {
            // Skip ClearBusy if the window has been closed — Avalonia
            // tolerates control mutations on a disposed visual tree
            // most days, but the warning surfaces as a first-chance
            // exception in debugger sessions and is genuinely wasted
            // work.
            if (!_closing) ClearBusy();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.IsVisible = true;
    }

    private void ClearError()
    {
        ErrorText.Text = "";
        ErrorBanner.IsVisible = false;
    }

    private void SetBusy(string status)
    {
        _busy = true;
        StatusInfo.Text = status;
        StatusRow.IsVisible = true;
        ConfirmButton.IsEnabled = false;
        // Keep Cancel ENABLED during busy — re-encrypting against a slow
        // cloud destination can take 5-30s and the user must always be
        // able to request a stop. Once mutation starts, cancellation waits
        // for the new key and saved credential to agree. The form
        // password fields stay locked so the user doesn't accidentally
        // edit while a re-encrypt is in flight.
        CancelButton.IsEnabled = true;
        NewPasswordBox.IsEnabled = false;
        ConfirmPasswordBox.IsEnabled = false;
    }

    private void ClearBusy()
    {
        _busy = false;
        StatusRow.IsVisible = false;
        ConfirmButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        NewPasswordBox.IsEnabled = true;
        ConfirmPasswordBox.IsEnabled = true;
    }

    private static string TailLines(string s, int maxLines)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lines = s.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= maxLines) return s.TrimEnd();
        return string.Join("\n", lines, lines.Length - maxLines, maxLines).TrimEnd();
    }
}
