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
public sealed class S56_EditorModeAndApply : ScenarioBase
{
    protected override string ScenarioName => "S56-editor-mode-and-apply";
    [AvaloniaFact] public Task Run_S56() => Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var destination = new Destination { Name = "Draft archive", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("draft-archive") };
        var seed = new Backup { Name = "Holiday documents", LastEditMode = BackupEditMode.Easy,
            FiltersText = "# Keep this custom rule\n*.private\n", SkipNotificationGraceDays = 14 };
        seed.SourcePaths.Add(ctx.MakeSource("draft-source", 61, 4096));
        seed.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "archive", ThreadsOverride = 3 });
        seed.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "legacy-chain", ThreadsOverride = 7 });
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(seed); });
        var savedBefore = ServiceLocator.Config.Current.Backups.Single().FiltersText;
        var owner = ctx.Driver.Show(new Window { Width = 1100, Height = 800 });
        // Recreate the real Edit entry point: this is the current config's
        // object, not a detached fixture that could conceal Cancel mutations.
        var currentBackup = ServiceLocator.Config.Current.Backups.Single();
        var flow = new BackupCreationWindow(currentBackup).ShowDialog(owner);

        var easy = await Child<OnboardingWindow>();
        Assert.Equal(14, ((OnboardingViewModel)easy.DataContext!).SkipNotificationGraceDays);
        Click(easy, "Advanced");
        var advanced = await Child<BackupEditorWindow>();
        var advancedVm = (BackupEditorViewModel)advanced.DataContext!;
        Assert.Equal(seed.SourcePaths, advancedVm.SourcePaths);
        Assert.Equal(14, advancedVm.SkipNotificationGraceDays);
        Assert.Equal(new[] { "archive", "legacy-chain" }, advancedVm.Targets.Select(t => t.StorageName));
        Assert.Equal(new int?[] { 3, 7 }, advancedVm.Targets.Select(t => t.ThreadsOverride));
        var name = advanced.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == seed.Name);
        name.Text = "Travel papers and photographs";
        ctx.Driver.Pump();

        advanced.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
        var customBlock = RecommendedFilters.CustomBlockHeader + "\n*.private\n" + RecommendedFilters.CustomBlockFooter;
        var oldBlock = RecommendedFilters.AutoBlockHeader + "\nstale-copied-rule/\n" + RecommendedFilters.AutoBlockFooter;
        advancedVm.FiltersText = customBlock + "\n" + oldBlock + "\n" +
            customBlock.Replace("*.private", "*.personal") + "\n" + oldBlock;
        ctx.Driver.Pump();
        Click(advanced, "Apply recommended exclusions");
        var merged = advancedVm.FiltersText;
        var custom = merged.IndexOf(RecommendedFilters.CustomBlockHeader, StringComparison.Ordinal);
        var customEnd = merged.IndexOf(RecommendedFilters.CustomBlockFooter, StringComparison.Ordinal);
        var generated = merged.IndexOf(RecommendedFilters.AutoBlockHeader, StringComparison.Ordinal);
        Assert.True(custom >= 0 && customEnd > custom && generated > customEnd);
        Assert.Contains("*.private", merged.Substring(custom, customEnd - custom));
        Assert.Contains("*.personal", merged.Substring(custom, customEnd - custom));
        Assert.Equal(1, merged.Split('\n').Count(line => line.Trim() == RecommendedFilters.CustomBlockHeader));
        Assert.Equal(1, merged.Split('\n').Count(line => line.Trim() == RecommendedFilters.AutoBlockHeader));
        Assert.DoesNotContain("stale-copied-rule/", merged);
        Assert.Contains("ntuser.dat*", merged);
        Click(advanced, "Apply recommended exclusions");
        Assert.Equal(merged, advancedVm.FiltersText);
        ctx.Snapshot(advanced, "applied-with-custom-rules-preserved",
            "The actual Apply button generated recommended rules beneath preserved custom rules; clicking Apply a second time left the text unchanged.");

        Click(advanced, "Easy");
        easy = await Child<OnboardingWindow>();
        var easyVm = (OnboardingViewModel)easy.DataContext!;
        Assert.Equal("Travel papers and photographs", easyVm.Working.Name);
        Assert.Equal(merged, easyVm.Working.FiltersText);
        Assert.Equal(14, easyVm.SkipNotificationGraceDays);
        AssertTargetsRetained(easyVm.Working);
        Click(easy, "Advanced");
        advanced = await Child<BackupEditorWindow>();
        advancedVm = (BackupEditorViewModel)advanced.DataContext!;
        Assert.Equal("Travel papers and photographs", advancedVm.Name);
        Assert.Equal(merged, advancedVm.FiltersText);
        advanced.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
        ctx.Driver.Pump();
        var inner = advanced.GetVisualDescendants().OfType<TabControl>()
            .Single(t => t.Items.OfType<TabItem>().Any(i => i.Header?.ToString() == "Advanced"));
        inner.SelectedIndex = 1;
        ctx.Driver.Pump();
        ctx.Snapshot(advanced, "round-trip-retains-name-grace-and-filters",
            "After actual Easy/Advanced toggle clicks, the Advanced filter editor retains the custom block above the recommended block and the user-edited draft.");
        ctx.Snapshot(advanced, "copied-custom-blocks-repaired",
            "After clicking Apply twice, both *.private and *.personal remain together in one custom block. The stale copied generated rule is gone. Cancel below must still preserve the saved backup.");
        Click(advanced, "Cancel");
        Assert.Null(await flow.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(savedBefore, ServiceLocator.Config.Current.Backups.Single().FiltersText);
        Assert.Equal("Holiday documents", ServiceLocator.Config.Current.Backups.Single().Name);
        Assert.Equal(savedBefore, currentBackup.FiltersText);
        Assert.Equal("Holiday documents", currentBackup.Name);
        AssertTargetsRetained(currentBackup);

        // Positive control: the same real Save button returns the edited draft.
        // The caller persists it with the same SaveBackupEdit boundary used by
        // BackupsViewModel.Edit; OS task registration is deliberately omitted.
        flow = new BackupCreationWindow(ServiceLocator.Config.Current.Backups.Single()).ShowDialog(owner);
        easy = await Child<OnboardingWindow>();
        Click(easy, "Advanced");
        advanced = await Child<BackupEditorWindow>();
        advancedVm = (BackupEditorViewModel)advanced.DataContext!;
        name = advanced.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == "Holiday documents");
        name.Text = "Saved travel documents";
        advancedVm.FiltersText = merged;
        advancedVm.SourcePaths.Add(ctx.MakeSource("second-source", 63, 2048));
        advancedVm.SkipNotificationGraceDays = 21;
        ctx.Driver.Pump();
        Assert.Single(ServiceLocator.Config.Current.Backups.Single().SourcePaths);
        Assert.Equal("Holiday documents", ServiceLocator.Config.Current.Backups.Single().Name);
        Click(advanced, "Save");
        var committed = await flow.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(committed);
        Assert.Equal("Saved travel documents", committed!.Name);
        AssertTargetsRetained(committed);
        Assert.Equal(2, committed.SourcePaths.Count);
        Assert.Equal("Holiday documents", ServiceLocator.Config.Current.Backups.Single().Name);
        ServiceLocator.Config.SaveBackupEdit(committed);
        var reopenedStore = new ConfigStore(); reopenedStore.Load();
        var saved = Assert.Single(reopenedStore.Current.Backups);
        Assert.Equal("Saved travel documents", saved.Name);
        Assert.Equal(merged, saved.FiltersText);
        Assert.Equal(21, saved.SkipNotificationGraceDays);
        AssertTargetsRetained(saved);
        Assert.Equal(2, saved.SourcePaths.Count);
        ctx.Report.RecordNote("Scope and negative/positive controls", "Real coordinator, windows and pointer clicks exercise mode switching, Apply, Cancel and Save. Cancel leaves the live original and persisted backup unchanged. Save returns the edited draft, and the normal persistence boundary survives a fresh ConfigStore load. Both existing storage names and per-target thread overrides on one destination are retained; no scheduler registration or backup operation runs.");

        static void AssertTargetsRetained(Backup backup)
        {
            Assert.Equal(new[] { "archive", "legacy-chain" }, backup.Targets.Select(t => t.StorageName));
            Assert.Equal(new int?[] { 3, 7 }, backup.Targets.Select(t => t.ThreadsOverride));
        }

        async Task<T> Child<T>() where T : Window
        {
            var stop = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < stop)
            {
                ctx.Driver.Pump();
                if (owner.OwnedWindows.OfType<T>().FirstOrDefault(w => w.IsVisible) is { } child)
                { child.UpdateLayout(); return child; }
                await Task.Delay(20);
            }
            throw new TimeoutException($"Coordinator did not open {typeof(T).Name}.");
        }

        void Click(Window window, string label)
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b =>
                b.IsEffectivelyVisible && b.Content?.ToString() == label && b.IsHitTestVisible);
            button.BringIntoView(); ctx.Driver.Pump(); window.UpdateLayout();
            // A tab switch or scroll changes layout before the headless
            // renderer updates its pointer hit-test scene. Render those
            // changes before clicking; keep using the real input path.
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
            var at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            var hit = window.InputHitTest(at);
            Assert.True(ReferenceEquals(hit, button) || hit is Visual visual && visual.GetVisualAncestors().Contains(button),
                $"The pointer at {at} for '{label}' would hit {hit?.GetType().Name ?? "nothing"}, not the requested button.");
            Assert.True(button.IsEffectivelyEnabled, $"'{label}' must be enabled before the click.");
            window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
            ctx.Driver.Pump();
        }
    }
}
