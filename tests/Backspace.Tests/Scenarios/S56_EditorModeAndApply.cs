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
        // No Apply button any more (U165 follow-up): ticking a group writes
        // its rules at once. The copied blocks have no group, so none is ticked.
        Assert.False(advancedVm.RecExcludeCachesAndJunk);
        ClickControl(advanced, advanced.FindControl<CheckBox>("CachesGroup")!);
        Assert.True(advancedVm.RecExcludeCachesAndJunk);
        var merged = advancedVm.FiltersText;
        var custom = merged.IndexOf(RecommendedFilters.CustomBlockHeader, StringComparison.Ordinal);
        var customEnd = merged.LastIndexOf(RecommendedFilters.CustomBlockFooter, StringComparison.Ordinal);
        var generated = merged.IndexOf(RecommendedFilters.AutoBlockHeader, StringComparison.Ordinal);
        // Both custom rules survive; only the first recommended block counts,
        // and the copied second one is gone.
        Assert.Contains("*.private", merged);
        Assert.Contains("*.personal", merged);
        Assert.True(custom >= 0 && generated > custom);
        Assert.Equal(1, merged.Split('\n').Count(line => line.Trim() == RecommendedFilters.AutoBlockHeader));
        // The rule written into the block by hand is not silently dropped by a
        // tick on another group: it stays, under its own heading.
        Assert.Equal(1, merged.Split('\n').Count(line => line.Trim() == "stale-copied-rule/"));
        Assert.Contains(RecommendedFilters.OwnHeading, merged);
        // A folder backup gets the rules that can match in it (U148): not the
        // drive-top ?:/pagefile.sys, but the caches.
        var recommendedBlock = merged.Substring(generated, merged.IndexOf(RecommendedFilters.AutoBlockFooter, StringComparison.Ordinal) - generated);
        Assert.DoesNotContain("pagefile.sys", merged);
        Assert.Contains("node_modules/", recommendedBlock);
        Assert.True(advancedVm.FiltersUnsaved);
        Assert.Equal(GroupStatus.WillBeAdded, advancedVm.CachesState.Status);
        // Unticking and ticking again leaves the text as it was.
        ClickControl(advanced, advanced.FindControl<CheckBox>("CachesGroup")!);
        Assert.DoesNotContain("node_modules/", advancedVm.FiltersText);
        Assert.Equal(GroupStatus.NotUsed, advancedVm.CachesState.Status);
        ClickControl(advanced, advanced.FindControl<CheckBox>("CachesGroup")!);
        Assert.Equal(merged, advancedVm.FiltersText);
        Assert.DoesNotContain(advanced.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() is "Apply recommended exclusions");
        ctx.Snapshot(advanced, "applied-with-custom-rules-preserved",
            "Clicking the caches box put its rules beneath the preserved custom rules at once: the group reads 'Will be added when you save', the page and the footer say the change is not saved yet. There is no Apply button.");

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
            "After ticking the caches group, both *.private and *.personal remain above the one recommended block; the copied second block is gone and the hand-written rule stays under its own heading. Cancel below must still preserve the saved backup.");
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
        ctx.Report.RecordNote("Scope and negative/positive controls", "Real coordinator, windows and pointer clicks exercise mode switching, ticking a recommended group, Cancel and Save. Cancel leaves the live original and persisted backup unchanged. Save returns the edited draft, and the normal persistence boundary survives a fresh ConfigStore load. Both existing storage names and per-target thread overrides on one destination are retained; no scheduler registration or backup operation runs.");

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

        void ClickControl(Window window, Control control)
        {
            control.BringIntoView(); ctx.Driver.Pump(); window.UpdateLayout();
            Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
            var at = control.TranslatePoint(new Point(12, 12), window)!.Value;
            var hit = window.InputHitTest(at);
            Assert.True(ReferenceEquals(hit, control) || hit is Visual visual && visual.GetVisualAncestors().Contains(control),
                $"The pointer at {at} would hit {hit?.GetType().Name ?? "nothing"}, not {control.Name}.");
            window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
            ctx.Driver.Pump();
        }
    }
}
