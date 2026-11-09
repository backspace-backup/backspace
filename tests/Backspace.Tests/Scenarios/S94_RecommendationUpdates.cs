using System;
using System.IO;
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
/// U165 (9 November 2026): recommended exclusions have three tickable groups,
/// Essentials first, each with its rules listed below it in Advanced mode.
/// A backup set up only in Easy mode takes Backspace's updated
/// recommendations by itself; clicking Advanced turns that off, and a box on
/// Filters > Recommended turns it on again. Otherwise the card says "New
/// filter suggestions: Review", which opens the suggestions with what each
/// changes in the backup; a hidden one stays hidden, Apply included.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S94_RecommendationUpdates : ScenarioBase
{
    protected override string ScenarioName => "S94-recommendation-updates";

    [AvaloniaFact] public async Task Run_S94() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        if (!OperatingSystem.IsWindows()) return;
        BackgroundBackupViewModel.SetupOverride = () => new NoAccess();
        try { await RunFlows(ctx); }
        finally { BackgroundBackupViewModel.SetupOverride = null; }
    }

    private static async Task RunFlows(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "Backup drive", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("drive") };
        ServiceLocator.Config.Update(cfg => cfg.Destinations.Add(dest));
        var owner = ctx.Driver.Show(new Window { Width = 1100, Height = 860 });

        // ---- 1. Easy mode only: Backspace keeps the list up to date ----
        var flow = new BackupCreationWindow(isFirstBackup: false).ShowDialog(owner);
        var wizard = await Child<OnboardingWindow>(ctx, owner);
        wizard.Width = 900; wizard.Height = 900;
        var easy = (OnboardingViewModel)wizard.DataContext!;
        easy.InstallShellExtension = false; // never register an Explorer verb from a test
        easy.SourceChips.Add(new SourceChipVm(ctx.MakeSource("documents", seed: 94, targetBytes: 20_000)));
        easy.SelectedDestinations.Add(easy.AvailableDestinations.Single(d => d.Id == dest.Id));
        easy.CurrentStep = 3;
        for (var n = 0; n < 50 && !easy.AdministratorAccess.Offered; n++) await ctx.Driver.PumpAsync(20);
        easy.AdministratorAccess.Allow = false; // no Windows prompt from a test
        Assert.True(easy.ApplyRecommendedExclusions);
        Assert.True(easy.Working.AutoApplyRecommendations);
        var note = wizard.FindControl<TextBlock>("RecommendedExclusionsNote")!;
        Assert.Contains("Backspace keeps this list up to date for you", note.Text);
        Assert.DoesNotContain("filter", note.Text, StringComparison.OrdinalIgnoreCase); // no jargon in Easy mode
        note.BringIntoView(); await ctx.Driver.PumpAsync(50);
        ctx.Snapshot(wizard, "easy-skip-junk",
            "Easy mode, last step: 'Skip junk for me' ticked, and below it in plain words that Backspace keeps this list up to date for you. No rules or paths are shown.");

        await easy.SaveOnlyCommand.ExecuteAsync(null);
        Assert.True(easy.SavedNoRun);
        wizard.Close();
        Assert.Equal(easy.Working.Id, (await flow.WaitAsync(TimeSpan.FromSeconds(5)))!.Id);
        var easyBackup = ServiceLocator.Config.Current.Backups.Single(b => b.Id == easy.Working.Id);
        Assert.True(easyBackup.AutoApplyRecommendations);
        Assert.True(RecommendedFilters.ReadChoice(easyBackup.FiltersText)!.Essentials);

        // A Backspace update brings a new cache rule: the next start adds it, no notice.
        ServiceLocator.Config.Update(c => c.Backups.Single(b => b.Id == easyBackup.Id).FiltersText =
            easyBackup.FiltersText.Replace("\nnode_modules/\n", "\n"));
        Assert.DoesNotContain("node_modules/", ServiceLocator.Config.Current.Backups.Single(b => b.Id == easyBackup.Id).FiltersText.Split('\n'));
        ServiceLocator.Config.Load();
        easyBackup = ServiceLocator.Config.Current.Backups.Single(b => b.Id == easyBackup.Id);
        Assert.Contains("node_modules/", easyBackup.FiltersText.Split('\n'));
        Assert.False(RecommendedFilters.HasSuggestions(easyBackup));
        ctx.Report.RecordNote("Easy only", "Saved through Easy mode with automatic updates on; a rule removed from its block (as a Backspace update adding it would leave it) was added back when settings loaded, with no card line.");

        // ---- 2. Clicking Advanced turns automatic updates off ----
        flow = new BackupCreationWindow(isFirstBackup: false).ShowDialog(owner);
        wizard = await Child<OnboardingWindow>(ctx, owner);
        easy = (OnboardingViewModel)wizard.DataContext!;
        easy.InstallShellExtension = false;
        easy.SourceChips.Add(new SourceChipVm(ctx.MakeSource("projects", seed: 95, targetBytes: 20_000)));
        easy.SelectedDestinations.Add(easy.AvailableDestinations.Single(d => d.Id == dest.Id));
        Assert.True(easy.Working.AutoApplyRecommendations);
        Click(ctx, wizard, "Advanced");
        var editor = await Child<BackupEditorWindow>(ctx, owner);
        editor.Width = 1040; editor.Height = 900;
        var vm = (BackupEditorViewModel)editor.DataContext!;
        Assert.False(vm.AutoApplyRecommendations);
        editor.ShowFilterSuggestions();
        await ctx.Driver.PumpAsync(200);
        Assert.True(vm.RecExcludeEssentials);
        var groups = new[] { "EssentialsGroup", "WindowsSystemGroup", "CachesGroup" }.Select(n => editor.FindControl<CheckBox>(n)!).ToList();
        Assert.All(groups, g => Assert.True(g.IsEffectivelyVisible));
        // Essentials first, then Windows system files, then caches.
        Assert.True(groups[0].TranslatePoint(default, editor)!.Value.Y < groups[1].TranslatePoint(default, editor)!.Value.Y);
        Assert.True(groups[1].TranslatePoint(default, editor)!.Value.Y < groups[2].TranslatePoint(default, editor)!.Value.Y);
        var expanders = editor.GetVisualDescendants().OfType<Expander>().Where(e => e.Classes.Contains("advanced") && e.IsEffectivelyVisible).ToList();
        Assert.Equal(3, expanders.Count);
        Assert.All(expanders, e => Assert.False(e.IsExpanded)); // collapsed by default
        var caches = expanders.Single(e => Equals(e.Header, vm.CachesRulesHeader));
        caches.IsExpanded = true;
        await ctx.Driver.PumpAsync(100);
        var listed = caches.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text!.Split('\n');
        Assert.Equal(RecommendedFilters.RulesFor(RecommendedFilters.Group.CachesAndJunk, vm.SourcePaths), listed);
        Assert.Contains("node_modules/", listed);
        // The block Apply wrote holds exactly those lines for the group.
        Assert.All(listed, l => Assert.Contains(l, vm.FiltersText.Replace("\r\n", "\n").Split('\n')));
        groups[0].BringIntoView(); await ctx.Driver.PumpAsync(50);
        ctx.Snapshot(editor, "advanced-recommended-three-groups",
            "Advanced > Filters > Recommended: Essentials (ticked, first), Windows system files, App caches and temporary files; each with 'Show the N rules it adds' below it, the caches list opened to show the actual lines (node_modules/, *.tmp, …).");

        var autoBox = editor.FindControl<CheckBox>("AutoApplyRecommendations")!;
        editor.FindControl<Border>("AutoApplyCard")!.BringIntoView(); await ctx.Driver.PumpAsync(50);
        Assert.False(autoBox.IsChecked);
        ctx.Snapshot(editor, "auto-apply-off-after-advanced",
            "After clicking Advanced: 'Apply Backspace's updated recommendations automatically' is unticked, with what on and off mean below it.");
        ClickControl(ctx, editor, autoBox);
        Assert.True(vm.AutoApplyRecommendations);
        editor.FindControl<Border>("AutoApplyCard")!.BringIntoView(); await ctx.Driver.PumpAsync(50);
        ctx.Snapshot(editor, "auto-apply-ticked-again",
            "The same box ticked again by a click: this backup would take Backspace's updated recommendations by itself.");
        Assert.True(vm.BuildSnapshot().AutoApplyRecommendations);
        // Back in Easy after Advanced: off again, and Easy says suggestions are reviewed.
        ClickControl(ctx, editor, autoBox);
        Click(ctx, editor, "Easy");
        wizard = await Child<OnboardingWindow>(ctx, owner);
        easy = (OnboardingViewModel)wizard.DataContext!;
        Assert.False(easy.Working.AutoApplyRecommendations);
        Assert.Contains("the backup's card offers them for you to review", easy.RecommendedExclusionsNote);
        wizard.Close();
        Assert.Null(await flow.WaitAsync(TimeSpan.FromSeconds(5)));
        // Ticked in Advanced and saved: reopening it in Advanced keeps it on.
        var keeps = new BackupEditorViewModel(new Backup { Name = "kept", AutoApplyRecommendations = true }, isNew: false);
        Assert.True(keeps.AutoApplyRecommendations);
        keeps.Dispose();
        // A backup started in Advanced mode starts with it off.
        var fresh = new BackupEditorViewModel(new Backup(), isNew: true);
        Assert.False(fresh.AutoApplyRecommendations);
        fresh.Dispose();

        // ---- 3. Not automatic: the card offers the suggestions ----
        var source = ctx.MakeSource("work", seed: 96, targetBytes: 10_000);
        Directory.CreateDirectory(Path.Combine(source, "site", "node_modules", "lib"));
        File.WriteAllBytes(Path.Combine(source, "site", "node_modules", "lib", "index.js"), new byte[40_000]);
        File.WriteAllBytes(Path.Combine(source, "site", "node_modules", "package.json"), new byte[2_000]);
        File.WriteAllBytes(Path.Combine(source, "draft.tmp"), new byte[5_000]);
        var full = RecommendedFilters.MergeIntoFilters("# Add your own rules here.",
            RecommendedFilters.RenderBlock(false, true, Array.Empty<RecommendedFilters.CloudSyncFolder>(), new[] { source })).Replace("\r\n", "\n");
        // As a Backspace update adding these three rules would leave the saved block.
        var saved = full.Replace("\nnode_modules/\n", "\n").Replace("\n*.tmp\n", "\n").Replace("\n*.crdownload\n", "\n");
        var advancedBackup = new Backup { Name = "Work projects", SourcePaths = { source }, FiltersText = saved };
        advancedBackup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        // Same change, caches group unticked: nothing to offer.
        var photos = ctx.MakeSource("photos", seed: 97, targetBytes: 10_000);
        var unticked = new Backup { Name = "Photos", SourcePaths = { photos },
            FiltersText = RecommendedFilters.MergeIntoFilters("", RecommendedFilters.RenderBlock(false, false, Array.Empty<RecommendedFilters.CloudSyncFolder>(), new[] { photos })).Replace("\r\n", "\n") };
        unticked.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        ServiceLocator.Config.Update(c => { c.Backups.Add(advancedBackup); c.Backups.Add(unticked); });
        ServiceLocator.Config.Load(); // as at the next start: nothing applied to a backup that isn't automatic
        Assert.Equal(saved, ServiceLocator.Config.Current.Backups.Single(b => b.Id == advancedBackup.Id).FiltersText);

        owner.Close();
        using var mainVm = new MainWindowViewModel();
        var main = ctx.Driver.Show(new MainWindow { DataContext = mainVm, Width = 1180, Height = 980 });
        mainVm.Backups.TestRestoreOwner = () => main; // headless tests have no desktop main window
        await ctx.Driver.PumpAsync(400);
        var cards = main.GetVisualDescendants().OfType<BackupCard>().ToList();
        Assert.Equal(3, cards.Count);
        BackupCard CardOf(string name) => cards.Single(c => ((BackupCardViewModel)c.DataContext!).Name == name);
        bool LineShown(BackupCard card) => card.FindControl<WrapPanel>("FilterSuggestionsLine")!.IsEffectivelyVisible;
        Assert.True(LineShown(CardOf("Work projects")));
        Assert.False(LineShown(CardOf("Photos")));
        Assert.False(LineShown(CardOf(easyBackup.Name)));
        var workCard = CardOf("Work projects");
        workCard.BringIntoView(); await ctx.Driver.PumpAsync(100);
        ctx.Snapshot(main, "card-new-filter-suggestions",
            "Your backups: only «Work projects» (caches ticked, automatic updates off) shows 'New filter suggestions: Review' under its buttons. «Photos» (caches unticked) and the Easy-mode backup (automatic) show nothing.");

        // ---- 4. Review: each suggestion with what it changes here ----
        var review = workCard.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Review");
        ClickControl(ctx, main, review);
        BackupEditorWindow? reviewing = null;
        for (var n = 0; n < 100 && reviewing is null; n++) { await ctx.Driver.PumpAsync(20); reviewing = main.OwnedWindows.OfType<BackupEditorWindow>().FirstOrDefault(); }
        Assert.NotNull(reviewing);
        reviewing!.Width = 1040; reviewing.Height = 900;
        var rvm = (BackupEditorViewModel)reviewing.DataContext!;
        var rows = rvm.RecommendedAdditions.ToList();
        Assert.Equal(new[] { "*.crdownload", "*.tmp", "node_modules/" }, rows.Select(r => r.Pattern).OrderBy(p => p, StringComparer.Ordinal));
        for (var n = 0; n < 250 && rows.Any(r => r.EffectLabel.Length == 0 || r.EffectLabel.StartsWith("Measuring", StringComparison.Ordinal)); n++)
            await ctx.Driver.PumpAsync(20);
        Assert.Equal("Leaves out 2 files (41.0 KB) of this backup.", rows.Single(r => r.Pattern == "node_modules/").EffectLabel);
        Assert.Equal("Leaves out 1 file (4.9 KB) of this backup.", rows.Single(r => r.Pattern == "*.tmp").EffectLabel);
        Assert.Equal("Matches nothing here today.", rows.Single(r => r.Pattern == "*.crdownload").EffectLabel);
        Assert.Equal(reviewing.FindControl<TabItem>("FiltersTab"), reviewing.FindControl<TabControl>("EditorTabs")!.SelectedItem);
        var panel = reviewing.FindControl<Border>("FilterSuggestions")!;
        Assert.True(panel.IsEffectivelyVisible);
        // Nothing changed yet.
        Assert.Equal(saved, rvm.FiltersText);
        ctx.Snapshot(reviewing, "review-panel-with-measured-effects",
            "Review opened Filters > Recommended at 'New filter suggestions': node_modules/ leaves out 2 files (41.0 KB), *.tmp 1 file (4.9 KB), *.crdownload matches nothing here today; each ticked, with Apply ticked changes and Hide all.");

        // Hide one, accept the others.
        reviewing.GetVisualDescendants().OfType<CheckBox>().Single(c => c.DataContext is RecommendationChangeChoice { Pattern: "*.crdownload" }).IsChecked = false;
        Click(ctx, reviewing, "Apply ticked changes");
        var after = rvm.FiltersText.Replace("\r\n", "\n").Split('\n');
        Assert.Contains("node_modules/", after);
        Assert.Contains("*.tmp", after);
        Assert.DoesNotContain("*.crdownload", after);
        Assert.False(rvm.HasRecommendationUpdate);
        Assert.Equal("1 suggested change is hidden for this backup.", rvm.HiddenRecommendationsText);
        // Apply rebuilds the whole block: the hidden one stays out.
        rvm.RecExcludeWindowsSystem = true;
        Click(ctx, reviewing, "Apply recommended exclusions");
        Assert.DoesNotContain("*.crdownload", rvm.FiltersText.Replace("\r\n", "\n").Split('\n'));
        Assert.True(RecommendedFilters.ReadChoice(rvm.FiltersText)!.WindowsSystem);
        Assert.True(rvm.RecommendedApplied);
        rvm.RecExcludeWindowsSystem = false;
        Click(ctx, reviewing, "Apply recommended exclusions");
        Click(ctx, reviewing, "Save");
        for (var n = 0; n < 100 && main.OwnedWindows.OfType<BackupEditorWindow>().Any(); n++) await ctx.Driver.PumpAsync(20);
        await ctx.Driver.PumpAsync(300);
        var stored = ServiceLocator.Config.Current.Backups.Single(b => b.Id == advancedBackup.Id);
        Assert.Contains("*.crdownload", stored.IgnoredRecommendationChanges);
        Assert.DoesNotContain("*.crdownload", stored.FiltersText.Split('\n'));
        Assert.False(stored.AutoApplyRecommendations);
        cards = main.GetVisualDescendants().OfType<BackupCard>().ToList();
        Assert.False(LineShown(CardOf("Work projects")));
        ctx.Snapshot(main, "card-after-review",
            "After the review was saved, «Work projects» no longer shows 'New filter suggestions': two were accepted, *.crdownload is hidden for this backup.");
        main.Close();
        ctx.Report.RecordNote("Limits", "Headless Avalonia. The automatic update is exercised through a settings load in the app process; the background service runs the same load code at its own start, which is not run here.");
    }

    private static async Task<T> Child<T>(ScenarioContext ctx, Window owner) where T : Window
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

    private static void Click(ScenarioContext ctx, Window window, string label)
        => ClickControl(ctx, window, window.GetVisualDescendants().OfType<Button>().Single(b =>
            b.IsEffectivelyVisible && b.Content?.ToString() == label && b.IsHitTestVisible));

    /// <summary>A real pointer click, after checking the pointer would reach the control.</summary>
    private static void ClickControl(ScenarioContext ctx, Window window, Control control)
    {
        control.BringIntoView(); ctx.Driver.Pump(); window.UpdateLayout();
        Screenshots.ScreenshotDiffTests.SettleInitialTransitions(window);
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        var hit = window.InputHitTest(at);
        Assert.True(ReferenceEquals(hit, control) || hit is Visual visual && visual.GetVisualAncestors().Contains(control),
            $"The pointer at {at} would hit {hit?.GetType().Name ?? "nothing"}, not {control.GetType().Name}.");
        Assert.True(control.IsEffectivelyEnabled);
        window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
        ctx.Driver.Pump();
    }

    private sealed class NoAccess : IBackgroundBackupSetup
    {
        public Task<BackgroundBackupStatus> InspectAsync() => Task.FromResult(new BackgroundBackupStatus(BackgroundBackupState.Off,
            "Without administrator access, backups can't include files other apps have open."));
        public Task ApplyAsync() => throw new InvalidOperationException("A test must not ask Windows for access.");
    }
}
