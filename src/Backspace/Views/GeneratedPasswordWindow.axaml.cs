using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Backspace.Services;

namespace Backspace.Views;

/// <summary>
/// A generated password, shown to be saved, then typed back from where it
/// was saved before it is used (U250, owner 14 November 2026: "make sure
/// user has taken a note of it, maybe ask him to enter it right after").
/// The window's result is true only after the password was typed back.
/// </summary>
public partial class GeneratedPasswordWindow : Window
{
    private readonly string _password;

    // Parameterless ctor exists only for the XAML compiler / designer.
    public GeneratedPasswordWindow() : this("") { }

    /// <param name="lossWarning">What is lost without it, for this password.</param>
    public GeneratedPasswordWindow(string password, string? lossWarning = null)
    {
        _password = password ?? "";
        InitializeComponent();
        PasswordBox.Text = _password;
        if (lossWarning is not null) LossWarning.Text = lossWarning;
        TypedBox.TextChanged += (_, _) =>
        {
            var typed = TypedBox.Text ?? "";
            UseButton.IsEnabled = PasswordGenerator.Matches(_password, typed);
            // Says so once the length is there, not on every keystroke before.
            MismatchText.IsVisible = !UseButton.IsEnabled && typed.Trim().Length >= _password.Length;
        };
        // Copied on open, so closing the window by mistake still leaves it somewhere.
        Opened += (_, _) => { _ = CopyAsync(); SavedButton.Focus(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
            else if (e.Key == Key.Enter && CheckStep.IsVisible && UseButton.IsEnabled) { Close(true); e.Handled = true; }
        };
    }

    /// <summary>A new password, saved and typed back, or null if the
    /// window was closed before that.</summary>
    public static async Task<string?> AskAsync(Window owner, string? lossWarning = null)
    {
        var password = PasswordGenerator.Create();
        var confirmed = await new GeneratedPasswordWindow(password, lossWarning).ShowDialog<bool>(owner);
        return confirmed ? password : null;
    }

    internal bool IsCheckingStep => CheckStep.IsVisible;

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        await CopyAsync();
        CopyLabel.Text = "Copied";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    internal async void OnSaved(object? sender, RoutedEventArgs e)
    {
        ShowStep.IsVisible = false;
        CheckStep.IsVisible = true;
        TypedBox.Text = "";
        TypedBox.Focus();
        // Typed back from the note, not pasted from Backspace's own copy.
        await ClearClipboardAsync();
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        CheckStep.IsVisible = false;
        ShowStep.IsVisible = true;
        CopyLabel.Text = "Copy";
    }

    private void OnUse(object? sender, RoutedEventArgs e)
    {
        if (PasswordGenerator.Matches(_password, TypedBox.Text ?? "")) Close(true);
    }

    private async Task CopyAsync()
    {
        try { if (Clipboard is { } clip) await clip.SetTextAsync(_password); }
        catch { /* clipboard unavailable: it can still be copied by hand */ }
    }

    /// <summary>Empties the clipboard if it still holds this password.</summary>
    private async Task ClearClipboardAsync()
    {
        try
        {
            if (Clipboard is { } clip && await clip.TryGetTextAsync() == _password)
                await clip.ClearAsync();
        }
        catch { /* clipboard unavailable */ }
    }
}
