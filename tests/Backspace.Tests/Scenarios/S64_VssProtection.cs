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

[Collection(ScenarioCollection.Name)]
public sealed class S64_VssProtection : ScenarioBase
{
    protected override string ScenarioName => "S64-vss-protection";
    [AvaloniaFact] public Task Run_S64() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var destination = new Destination { Name = "Local archive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("archive") };
        var backup = new Backup { Name = "Family documents", SourcePaths = { ctx.MakeSource("documents", 74, 1024) },
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        Assert.True(backup.UseVss);
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        using var mainVm = new MainWindowViewModel();
        var main = ctx.Driver.Show(new MainWindow { DataContext = mainVm, Width = 1100, Height = 760 });
        Assert.False(mainVm.HasLimitedProtection);
        var editor = new BackupEditorWindow(new Backup { Id = backup.Id, Name = backup.Name, SourcePaths = backup.SourcePaths,
            Targets = backup.Targets }, false);
        var saving = editor.ShowDialog<Backup?>(main);
        ctx.Driver.Pump();
        var vm = (BackupEditorViewModel)editor.DataContext!;
        editor.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 0;
        vm.IsAdvancedOpen = true;
        ctx.Driver.Pump();
        var snapshots = editor.FindControl<CheckBox>("UseVssCheck")!;
        Pointer(editor, snapshots);
        Assert.False(vm.UseVss);
        Assert.True(vm.ShowVssWarning);
        Click(editor, "Save");
        Assert.False(saving.IsCompleted);
        Assert.Contains("accept the locked-file limitation", vm.SummaryError);
        var acknowledgement = editor.FindControl<CheckBox>("VssLimitationAcknowledgement")!;
        acknowledgement.BringIntoView(); ctx.Driver.Pump();
        ctx.Snapshot(editor, "snapshots-off-requires-acceptance",
            "Turning snapshots off exposes a prominent warning; Save is rejected until the user explicitly accepts missing or inconsistent open files.");
        Pointer(editor, acknowledgement);
        Assert.True(vm.VssDisabledAcknowledged);
        Pointer(editor, snapshots); // Re-enable, then disable: old consent must not carry over.
        Pointer(editor, snapshots);
        Assert.False(vm.VssDisabledAcknowledged);
        Pointer(editor, acknowledgement);
        Click(editor, "Save");
        var saved = await saving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(saved);
        Assert.False(saved.UseVss);
        Assert.True(saved.VssDisabledAcknowledged);
        // Preserve the original identity when writing the accepted draft.
        saved.Id = backup.Id;
        ServiceLocator.Config.SaveBackupEdit(saved);
        ctx.Driver.Pump();
        Assert.True(mainVm.HasLimitedProtection);
        ctx.Snapshot(main, "persistent-limited-protection",
            "The bottom of the sidebar clearly says Limited protection and explains that snapshots are off.");
        // Reproduce the narrow sidebar from the reported layout. The action
        // and arrow have their own row, so wrapping the explanation cannot
        // strand an arrow beneath the text or clip the action.
        main.Width = 920; main.Height = 540; ctx.Driver.Pump();
        var protection = main.FindControl<Button>("LimitedProtectionButton")!;
        foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
        {
            main.RequestedThemeVariant = theme; ctx.Driver.Pump(); main.UpdateLayout();
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(main);
            foreach (var name in new[] { "ProtectionCardHeading", "ProtectionCardExplanation", "ProtectionCardAction" })
            {
                var text = main.FindControl<TextBlock>(name)!;
                var at = text.TranslatePoint(default, protection)!.Value;
                Assert.True(at.X >= 0 && at.Y >= 0);
                Assert.True(at.X + text.Bounds.Width <= protection.Bounds.Width);
                Assert.True(at.Y + text.Bounds.Height <= protection.Bounds.Height);
                Assert.True(text.TextLayout.Height <= text.Bounds.Height + 1, name + " must not clip its text");
                if (name != "ProtectionCardExplanation") Assert.Single(text.TextLayout.TextLines);
            }
            var action = main.FindControl<TextBlock>("ProtectionCardAction")!;
            var arrow = main.FindControl<PathIcon>("ProtectionCardArrow")!;
            var actionAt = action.TranslatePoint(default, protection)!.Value;
            var arrowAt = arrow.TranslatePoint(default, protection)!.Value;
            Assert.True(arrowAt.X >= actionAt.X + action.Bounds.Width);
            Assert.InRange(arrowAt.Y + arrow.Bounds.Height / 2, actionAt.Y, actionAt.Y + action.Bounds.Height);
            Assert.True(protection.TranslatePoint(default, main)!.Value.Y >= 40, "The warning must fit above the footer.");
            ctx.Snapshot(main, theme == Avalonia.Styling.ThemeVariant.Light ? "limited-protection-small-light" : "limited-protection-small-dark",
                "At the 920 × 540 minimum window, the warning has a clear title, explanation and separate Review protection action; the arrow stays beside the action.");
        }
        main.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; ctx.Driver.Pump();
        Pointer(main, main.FindControl<Button>("LimitedProtectionButton")!);
        var dialog = main.OwnedWindows.OfType<DestructiveConfirmWindow>().Single(w => w.IsVisible);
        ctx.Driver.Pump();
        ctx.Snapshot(dialog, "explanation-and-recovery",
            "The dialog names the affected backup, explains missing or inconsistent files, and offers Enable snapshots.");
        Click(dialog, "Enable snapshots"); ctx.Driver.Pump();
        Assert.True(ServiceLocator.Config.Current.Backups.Single().UseVss);
        Assert.False(mainVm.HasLimitedProtection);
        var store = new ConfigStore(); store.Load();
        Assert.True(store.Current.Backups.Single().UseVss);
        Assert.False(store.Current.Backups.Single().VssDisabledAcknowledged);
        ctx.Report.RecordNote("Scope", "Actual controls receive pointer input in headless Avalonia. Config is reloaded from disk. Negative cases reject Save without consent and invalidate old consent when snapshots are toggled. Real UAC, locked-file recovery and cancellation are recorded separately by the native Windows probe.");

        void Click(Window w, string label) => Pointer(w, w.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && b.Content?.ToString() == label));
        void Pointer(Window w, Control control)
        {
            control.BringIntoView(); ctx.Driver.Pump(); w.UpdateLayout();
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(w);
            var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), w)!.Value;
            w.MouseDown(p, MouseButton.Left); w.MouseUp(p, MouseButton.Left); ctx.Driver.Pump();
        }
    }
}
