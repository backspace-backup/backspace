using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
/// U206 (owner, 12 November 2026): people may forget to click Save at the
/// bottom of Settings. Settings has no Save button: each change saves by
/// itself and a small "Saved" shows where the page is scrolled to, a failure
/// in red until a save works. The backup and destination editors ask "Save
/// your changes?" (Save, Don't save, Keep editing) when closed with unsaved
/// changes, and close without asking when nothing changed.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S105_SavingWithoutASaveButton : ScenarioBase
{
    protected override string ScenarioName => "S105-saving-without-a-save-button";

    [AvaloniaFact] public async Task Run_S105() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        // Quitting closes editors without asking; this scenario isn't quitting.
        var quitting = Backspace.App.QuitRequested;
        Backspace.App.QuitRequested = false;
        try { await Steps(ctx); }
        finally { Backspace.App.QuitRequested = quitting; }
    }

    private static async Task Steps(ScenarioContext ctx)
    {
        // ---- Settings ----
        using (var main = new MainWindowViewModel())
        {
            var host = ctx.Driver.Show(new MainWindow { Width = 1100, Height = 760, DataContext = main });
            main.SelectedNav = NavItem.Settings;
            await ctx.Driver.PumpAsync(300);
            var settings = host.GetVisualDescendants().OfType<SettingsView>().Single();
            Assert.DoesNotContain(settings.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Save settings"));
            var saved = settings.FindControl<Border>("SettingsSavedNotice")!;
            Assert.False(saved.IsVisible);
            // A change far down the page saves at once and says so in view.
            var scroller = settings.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Avalonia.Vector(0, scroller.Extent.Height);
            var before = ServiceLocator.Config.Current.Limits.Total;
            main.Settings.BackupsInTotal = before == 3 ? 4 : 3;
            await ctx.Driver.PumpAsync(100);
            Assert.Equal(main.Settings.BackupsInTotal, ServiceLocator.Config.Current.Limits.Total);
            Assert.True(saved.IsEffectivelyVisible);
            var inView = saved.TranslatePoint(new Avalonia.Point(0, saved.Bounds.Height), host)!.Value;
            Assert.InRange(inView.Y, 0, host.Bounds.Height);
            ctx.Snapshot(host, "saved after a change", "Settings scrolled to the bottom, no Save button: a small green \"Saved\" at the bottom right after a change, which was saved at once.");
            await Task.Delay(1800); ctx.Driver.Pump();
            Assert.False(saved.IsVisible);
            // A failed save stays, in red, until a save works.
            main.Settings.SaveStatus = "Settings weren't saved: the disk is full. Your change is still shown; it is saved with the next change that works.";
            await ctx.Driver.PumpAsync(100);
            Assert.True(settings.FindControl<Border>("SettingsSaveFailed")!.IsEffectivelyVisible);
            ctx.Snapshot(host, "a save that failed", "A red notice along the bottom of Settings says the change wasn't saved and why; it stays until a save works.");
            main.Settings.BackupsInTotal = before;
            await ctx.Driver.PumpAsync(100);
            Assert.False(settings.FindControl<Border>("SettingsSaveFailed")!.IsVisible);
            host.Close();
        }

        // ---- The backup editor ----
        var dest = new Destination { Name = "Archive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("repo") };
        var backup = new Backup { Name = "Documents", SourcePaths = { ctx.MakeSource("docs", targetBytes: 2048) } };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });
        Backup Saved() => ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);

        // Nothing changed: closes without asking.
        var owner = ctx.Driver.Show(new Window { Width = 400, Height = 300 });
        var editor = new BackupEditorWindow(Clone(Saved()), isNew: false);
        var closed = editor.ShowDialog<Backup?>(owner);
        await ctx.Driver.PumpAsync(800);
        editor.Close();
        await ctx.Driver.PumpAsync(100);
        Assert.True(closed.IsCompleted, "An unchanged editor must close without asking.");
        Assert.Empty(owner.OwnedWindows.OfType<UnsavedChangesWindow>());

        // Changed, then Escape: asked. Keep editing keeps it open.
        editor = new BackupEditorWindow(Clone(Saved()), isNew: false) { Width = 980, Height = 760 };
        closed = editor.ShowDialog<Backup?>(owner);
        await ctx.Driver.PumpAsync(300);
        ((BackupEditorViewModel)editor.DataContext!).Name = "Documents and photos";
        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await ctx.Driver.PumpAsync(200);
        var ask = editor.OwnedWindows.OfType<UnsavedChangesWindow>().Single();
        Assert.Equal("Save your changes?", ask.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "Heading").Text);
        ctx.Snapshot(ask, "save your changes", "\"Save your changes?\": \"You changed this backup's settings. Closing without saving discards these changes.\" with Keep editing, Don't save and Save.");
        Click(ask, "KeepEditingButton");
        await ctx.Driver.PumpAsync(200);
        Assert.False(closed.IsCompleted);
        Assert.Equal("Documents", Saved().Name);

        // Don't save: closes, nothing saved.
        editor.Close();
        await ctx.Driver.PumpAsync(200);
        Click(editor.OwnedWindows.OfType<UnsavedChangesWindow>().Single(), "DiscardButton");
        await ctx.Driver.PumpAsync(200);
        Assert.True(closed.IsCompleted);
        Assert.Null(await closed);
        Assert.Equal("Documents", Saved().Name);

        // Save: saves, then closes with the result.
        editor = new BackupEditorWindow(Clone(Saved()), isNew: false) { Width = 980, Height = 760 };
        closed = editor.ShowDialog<Backup?>(owner);
        await ctx.Driver.PumpAsync(300);
        ((BackupEditorViewModel)editor.DataContext!).Name = "Documents and photos";
        editor.Close();
        await ctx.Driver.PumpAsync(200);
        Click(editor.OwnedWindows.OfType<UnsavedChangesWindow>().Single(), "SaveButton");
        for (var i = 0; i < 100 && !closed.IsCompleted; i++) await ctx.Driver.PumpAsync(50);
        Assert.True(closed.IsCompleted, "Save must save and close the editor.");
        Assert.Equal("Documents and photos", (await closed)!.Name);

        // ---- The destination editor ----
        var destEditor = new DestinationEditorWindow(Clone(dest), isNew: false);
        var destClosed = destEditor.ShowDialog<Destination?>(owner);
        await ctx.Driver.PumpAsync(500);
        destEditor.Close();
        await ctx.Driver.PumpAsync(100);
        Assert.True(destClosed.IsCompleted, "An unchanged destination editor must close without asking.");
        destEditor = new DestinationEditorWindow(Clone(dest), isNew: false);
        destClosed = destEditor.ShowDialog<Destination?>(owner);
        await ctx.Driver.PumpAsync(300);
        ((DestinationEditorViewModel)destEditor.DataContext!).Name = "Archive drive";
        destEditor.Close();
        await ctx.Driver.PumpAsync(200);
        var destAsk = destEditor.OwnedWindows.OfType<UnsavedChangesWindow>().Single();
        Assert.Contains("this destination", string.Join(" ", destAsk.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)));
        Click(destAsk, "DiscardButton");
        await ctx.Driver.PumpAsync(200);
        Assert.True(destClosed.IsCompleted);
        Assert.Equal("Archive", ServiceLocator.Config.Current.Destinations.Single(d => d.Id == dest.Id).Name);
        owner.Close();

        void Click(Window window, string name)
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            ctx.Driver.Pump();
        }
        static T Clone<T>(T value) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value))!;
    }
}
