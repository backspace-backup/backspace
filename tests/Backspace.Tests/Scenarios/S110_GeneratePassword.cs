using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U250 (owner, 14 November 2026): "When asking for a vault or storage
/// password, it should offer to generate a password for the user (and make
/// sure user has taken a note of it, maybe ask him to enter it right
/// after)." In a new encrypted destination: Generate shows a strong
/// password, then asks for it back from where it was saved; a wrong one is
/// refused, the right one fills the storage password. Closing the window
/// leaves the field empty, and Save with an empty password asks the same
/// way before anything is saved. The change-password and saved-passwords
/// windows offer Generate too.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S110_GeneratePassword : ScenarioBase
{
    protected override string ScenarioName => "S110-generate-password";
    [AvaloniaFact] public Task Run_S110() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var editor = new DestinationEditorWindow(new Destination { Name = "USB drive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo") }, isNew: true) { Width = 820, Height = 900 };
        ctx.Driver.Show(editor);
        var vm = (DestinationEditorViewModel)editor.DataContext!;
        vm.Encrypted = true;
        ctx.Driver.Pump();

        // Generate, then close the window: nothing is filled in.
        await ClickAsync(ctx, editor, Named<Button>(editor, "GenerateStoragePasswordButton"));
        var dialog = await DialogAsync(ctx, editor);
        dialog.Close(false);
        await ctx.Driver.PumpAsync(100);
        Assert.True(string.IsNullOrEmpty(vm.StoragePasswordInput), "Closing the window must not fill in the password.");

        // Generate again, and go through both steps.
        await ClickAsync(ctx, editor, Named<Button>(editor, "GenerateStoragePasswordButton"));
        dialog = await DialogAsync(ctx, editor);
        var password = Named<TextBox>(dialog, "PasswordBox").Text!;
        Assert.Matches("^[a-z2-9]{5}(-[a-z2-9]{5}){4}$", password);
        ctx.Snapshot(dialog, "generated password", "Step 1: a strong password in five groups, with Copy, to save somewhere safe.");
        await ClickAsync(ctx, dialog, Named<Button>(dialog, "SavedButton"));
        Assert.True(dialog.IsCheckingStep);
        Assert.False(Named<TextBox>(dialog, "PasswordBox").IsEffectivelyVisible, "The password must be hidden while it is typed back.");

        // A wrong password is refused.
        var typed = Named<TextBox>(dialog, "TypedBox");
        typed.Focus();
        dialog.KeyTextInput(password[..^1] + (password[^1] == 'a' ? 'b' : 'a'));
        ctx.Driver.Pump();
        Assert.False(Named<Button>(dialog, "UseButton").IsEnabled);
        Assert.True(Named<TextBlock>(dialog, "MismatchText").IsVisible);
        ctx.Snapshot(dialog, "wrong password typed back", "Step 2: a password that differs by one character is refused; Use this password stays disabled.");

        // The right one is accepted and fills the storage password.
        typed.Text = "";
        dialog.KeyTextInput(password);
        ctx.Driver.Pump();
        Assert.True(Named<Button>(dialog, "UseButton").IsEnabled);
        await ClickAsync(ctx, dialog, Named<Button>(dialog, "UseButton"));
        Assert.True(ctx.Driver.WaitFor(() => !dialog.IsVisible, 5_000), "The window did not close.");
        Assert.Equal(password, vm.StoragePasswordInput);
        ctx.Snapshot(editor, "password filled in", "The destination editor after typing it back: the storage password is filled in (masked).");

        // Save with an empty password asks the same way first; closing saves nothing.
        vm.StoragePasswordInput = "";
        await ClickAsync(ctx, editor, editor.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == "Save"));
        dialog = await DialogAsync(ctx, editor);
        dialog.Close(false);
        await ctx.Driver.PumpAsync(200);
        Assert.True(editor.IsVisible, "Closing the password window must not save the destination.");
        Assert.DoesNotContain(ServiceLocator.Config.Current.Destinations, d => d.Name == "USB drive");
        editor.Close();

        // The other places a password is chosen offer Generate too.
        var change = new ChangeStoragePasswordWindow { Width = 640, Height = 560 };
        ctx.Driver.Show(change);
        Assert.True(Named<Button>(change, "GenerateButton").IsEffectivelyVisible);
        ctx.Snapshot(change, "change storage password", "Change storage password: Generate beside the new password.");
        change.Close();
        var vault = new VaultPasswordWindow(new VaultPromptRequest(VaultPromptMode.Create, "", _ => Task.FromResult<string?>(null))) { Width = 640 };
        ctx.Driver.Show(vault);
        Assert.True(Named<Button>(vault, "GenerateButton").IsEffectivelyVisible);
        ctx.Snapshot(vault, "saved passwords password", "Choosing the saved-passwords password: Generate beside it.");
        vault.Close();
        var unlock = new VaultPasswordWindow(new VaultPromptRequest(VaultPromptMode.Unlock, "", _ => Task.FromResult<string?>(null))) { Width = 640 };
        ctx.Driver.Show(unlock);
        Assert.False(Named<Button>(unlock, "GenerateButton").IsVisible, "Entering an existing password offers no Generate.");
        unlock.Close();
    }

    private static T Named<T>(Window window, string name) where T : Control
        => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static async Task<GeneratedPasswordWindow> DialogAsync(ScenarioContext ctx, Window owner)
    {
        for (var i = 0; i < 100; i++)
        {
            if (owner.OwnedWindows.OfType<GeneratedPasswordWindow>().FirstOrDefault(w => w.IsVisible) is { } dialog)
            {
                await ctx.Driver.PumpAsync(100);
                return dialog;
            }
            await ctx.Driver.PumpAsync(50);
        }
        throw new TimeoutException("The generated-password window did not open.");
    }

    private static async Task ClickAsync(ScenarioContext ctx, Window window, Control control)
    {
        window.UpdateLayout();
        control.BringIntoView();
        await ctx.Driver.PumpAsync(50);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        await ctx.Driver.PumpAsync(100);
    }
}
