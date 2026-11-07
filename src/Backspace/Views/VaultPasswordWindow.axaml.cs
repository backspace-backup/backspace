using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Backspace.Services;

namespace Backspace.Views;

/// <summary>
/// The saved-passwords window (U153): choose a password when the first
/// credential is saved, enter it once on a new computer, change it, move the
/// earlier per-computer files into the portable vault, or start over when
/// the vault can't be opened. The action runs inside the window, so a wrong
/// password is explained without closing it. Closing without finishing
/// saves nothing.
/// </summary>
public partial class VaultPasswordWindow : Window
{
    private readonly VaultPromptRequest _request;
    private bool _busy;
    public bool Completed { get; private set; }

    /// <summary>Designer only.</summary>
    public VaultPasswordWindow() : this(new VaultPromptRequest(VaultPromptMode.Create, "", _ => Task.FromResult<string?>(null))) { }

    public VaultPasswordWindow(VaultPromptRequest request)
    {
        _request = request;
        InitializeComponent();
        Apply(request.Mode);
        ReasonBlock.Text = request.Reason;
        ReasonBlock.IsVisible = !string.IsNullOrWhiteSpace(request.Reason);
        PasswordBox.TextChanged += (_, _) => UpdateEmptyNotice();
        ConfirmBox.TextChanged += (_, _) => UpdateEmptyNotice();
        UpdateEmptyNotice();
        Opened += (_, _) => PasswordBox.Focus();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) { Close(false); e.Handled = true; }
            else if (e.Key == Key.Enter && !_busy) { OnConfirm(this, new RoutedEventArgs()); e.Handled = true; }
        };
    }

    internal const string NoPasswordWarning =
        "Without a password, your saved passwords are only obscured, not protected: anyone who gets a copy of " +
        "the Backspace folder (for example from your Dropbox) can read them, along with access to your backups.";

    private bool AllowsEmpty => _request.Mode is VaultPromptMode.Create or VaultPromptMode.Change
        or VaultPromptMode.Convert or VaultPromptMode.StartOver;
    private bool Confirms => AllowsEmpty;

    private void Apply(VaultPromptMode mode)
    {
        const string travels = "They travel with the Backspace folder, so a copy on another computer works after you enter this password there once.";
        (string title, string intro, string action, string cancel) = mode switch
        {
            VaultPromptMode.Create => ("Choose a password for your saved passwords",
                "Backspace keeps your storage passwords, cloud sign-ins and email keys in one file beside the app, locked with this password. "
                + travels + " On this computer, Backspace remembers it.",
                "Save password", "Cancel"),
            VaultPromptMode.Unlock => ("Enter your saved-passwords password",
                "This computer doesn't have the key to your saved passwords yet. Enter the password once; Backspace remembers it on this computer.",
                "Unlock", "Not now"),
            VaultPromptMode.Change => ("Change the password for your saved passwords",
                "Your saved passwords stay as they are; only the password that locks them changes. Other computers that already "
                + "use this Backspace folder keep working without it.",
                "Change password", "Cancel"),
            VaultPromptMode.Convert => ("Your saved passwords can now travel with Backspace",
                "Until now, your storage passwords and cloud sign-ins could only be opened on this computer, by this Windows account. "
                + "Backspace now keeps them in one file beside the app, locked with a password you choose. " + travels
                + " Nothing is lost: Backspace moves them and checks every one before removing the old files.",
                "Move my saved passwords", "Not now"),
            _ => ("Your saved passwords can't be opened",
                (_request.Detail ?? "The file holding them is damaged.")
                + "\n\nYou can start over: Backspace keeps the old file beside a new, empty one. You'll then enter "
                + "your storage passwords and sign in to cloud destinations again.",
                "Start over", "Cancel"),
        };
        TitleBlock.Text = title;
        IntroBlock.Text = intro;
        ConfirmButton.Content = action;
        CancelButton.Content = cancel;
        PasswordLabel.Text = mode is VaultPromptMode.Change or VaultPromptMode.StartOver ? "NEW PASSWORD" : "PASSWORD";
        ConfirmPanel.IsVisible = Confirms;
        NoPasswordText.Text = NoPasswordWarning;
    }

    private void UpdateEmptyNotice()
    {
        var empty = string.IsNullOrEmpty(PasswordBox.Text) && string.IsNullOrEmpty(ConfirmBox.Text);
        NoPasswordNotice.IsVisible = AllowsEmpty && empty;
        if (AllowsEmpty && _request.Mode != VaultPromptMode.StartOver)
            ConfirmButton.Content = empty ? "Continue without a password" : _request.Mode switch
            {
                VaultPromptMode.Create => "Save password",
                VaultPromptMode.Change => "Change password",
                _ => "Move my saved passwords",
            };
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close(false);
    }

    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_busy || !ConfirmButton.IsEnabled) return;
        ErrorBanner.IsVisible = false;
        var password = PasswordBox.Text ?? "";
        if (Confirms && password != (ConfirmBox.Text ?? ""))
        {
            ShowError("The two passwords don't match. Type them again.");
            ConfirmBox.Focus();
            return;
        }
        SetBusy(true);
        string? error;
        try { error = await _request.Submit(password); }
        catch (Exception ex)
        {
            AppLogger.For<VaultPasswordWindow>().Warning(ex, "The saved-passwords action failed");
            error = ex.Message;
        }
        SetBusy(false);
        if (error is null)
        {
            Completed = true;
            Close(true);
            return;
        }
        ShowError(error);
        PasswordBox.Text = "";
        ConfirmBox.Text = "";
        PasswordBox.Focus();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        StatusRow.IsVisible = busy;
        StatusInfo.Text = _request.Mode == VaultPromptMode.Convert ? "Moving and checking your saved passwords…" : "Working…";
        ConfirmButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        ConfirmBox.IsEnabled = !busy;
    }

    internal void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.IsVisible = true;
    }

    // ---- test and scenario hooks ----
    internal void EnterPassword(string password, string? confirm = null)
    {
        PasswordBox.Text = password;
        ConfirmBox.Text = confirm ?? password;
    }
    internal Task ConfirmAsync()
    {
        OnConfirm(this, new RoutedEventArgs());
        return WaitIdleAsync();
    }
    internal async Task WaitIdleAsync()
    {
        while (_busy) await Task.Delay(25);
    }
    internal string? Error => ErrorBanner.IsVisible ? ErrorText.Text : null;
}

/// <summary>Shows <see cref="VaultPasswordWindow"/> over the window in front.</summary>
public sealed class VaultPasswordPrompt : IVaultPrompt
{
    public Task<bool> AskAsync(VaultPromptRequest request)
        => Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new VaultPasswordWindow(request);
            var owner = Owner();
            if (owner is null)
            {
                var closed = new TaskCompletionSource();
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                await closed.Task;
                return window.Completed;
            }
            await window.ShowDialog<bool?>(owner);
            return window.Completed;
        });

    private static Window? Owner()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return null;
        return desktop.Windows.LastOrDefault(w => w.IsActive && w.IsVisible)
            ?? desktop.Windows.LastOrDefault(w => w.IsVisible);
    }
}
