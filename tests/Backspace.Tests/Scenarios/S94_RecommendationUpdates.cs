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
/// changes in the backup; a hidden one stays hidden, a ticked group included.
/// U165 follow-up (10 November 2026): no Apply button. A tick writes its
/// group at once; each group says "In place", "Will be added / removed /
/// change when you save" or "Off", and the page and the footer say when
/// something waits for Save. Saved, reopened, unticked, saved and reopened.
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
        // A new backup: every ticked group is written at once and waits for Save.
        Assert.All(new[] { vm.EssentialsState, vm.WindowsSystemState, vm.CachesState }, s => Assert.Equal(GroupStatus.WillBeAdded, s.Status));
        Assert.Equal("Will be added when you save", StatusShown(editor, "CachesStatus"));
        Assert.True(editor.FindControl<TextBlock>("RecommendedNewLine")!.IsEffectivelyVisible);
        // A folder backup: none of the Essentials can be inside it, said in words, not "none".
        Assert.False(vm.EssentialsState.HasRules);
        Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == vm.EssentialsState.NoRulesText);
        var expanders = editor.GetVisualDescendants().OfType<Expander>().Where(e => e.Classes.Contains("advanced") && e.IsEffectivelyVisible).ToList();
        Assert.Equal(2, expanders.Count);
        Assert.All(expanders, e => Assert.False(e.IsExpanded)); // collapsed by default
        var caches = expanders.Single(e => Equals(e.Header, vm.CachesState.RulesHeader));
        Assert.Matches(@"^Its \d+ rules$", vm.CachesState.RulesHeader);
        caches.IsExpanded = true;
        await ctx.Driver.PumpAsync(100);
        var listed = caches.GetVisualDescendants().OfType<SelectableTextBlock>().Single().Text!.Split('\n');
        Assert.Equal(RecommendedFilters.GroupRules(vm.FiltersText, RecommendedFilters.Group.CachesAndJunk), listed);
        Assert.Contains("node_modules/", listed);
        // The list is exactly what the filters hold for the group, nothing to apply.
        Assert.All(listed, l => Assert.Contains(l, vm.FiltersText.Replace("\r\n", "\n").Split('\n')));
        Assert.DoesNotContain(editor.GetVisualDescendants().OfType<Button>(), b => b.Content as string is "Apply recommended exclusions");
        groups[0].BringIntoView(); await ctx.Driver.PumpAsync(50);
        ctx.Snapshot(editor, "advanced-recommended-three-groups",
            "A new backup, Filters > Recommended: Essentials (first; 'Nothing to leave out in this backup's folders'), Windows system files and App caches, each with an amber 'Will be added when you save'; the caches list opened as 'Its N rules' showing the actual lines. No Apply button: Save keeps them.");

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

        // ---- 2b. Saved, reopened, a group unticked, saved and reopened again ----
        // The owner's question (10 November 2026): after saving, is there
        // anything left to do? Every group says so on its own line.
        var homeOwner = ctx.Driver.Show(new Window { Width = 1100, Height = 860 });
        var fresh2 = new Backup { Name = "Home folder", SourcePaths = { ctx.MakeSource("home", seed: 98, targetBytes: 20_000) },
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } } };
        var creating = new BackupEditorWindow(fresh2, isNew: true) { Width = 1040, Height = 900 };
        var creatingDone = creating.ShowDialog<Backup?>(homeOwner);
        await ctx.Driver.PumpAsync(200);
        ((BackupEditorViewModel)creating.DataContext!).AdministratorAccess.Allow = false; // no Windows prompt from a test
        Click(ctx, creating, "Save");
        var created = await creatingDone.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(created);
        ServiceLocator.Config.Update(c => c.Backups.Add(created!));

        async Task<(BackupEditorWindow Window, BackupEditorViewModel Vm, Task<Backup?> Done)> Reopen()
        {
            var stored = ServiceLocator.Config.Current.Backups.Single(b => b.Id == created!.Id);
            var window = new BackupEditorWindow(stored, isNew: false) { Width = 1040, Height = 900 };
            var done = window.ShowDialog<Backup?>(homeOwner);
            window.FindControl<TabControl>("EditorTabs")!.SelectedItem = window.FindControl<TabItem>("FiltersTab");
            await ctx.Driver.PumpAsync(250);
            return (window, (BackupEditorViewModel)window.DataContext!, done);
        }

        var (reopened, rvm2, reopenedDone) = await Reopen();
        Assert.All(new[] { rvm2.EssentialsState, rvm2.WindowsSystemState, rvm2.CachesState }, s => Assert.Equal(GroupStatus.InPlace, s.Status));
        Assert.Equal("In place", StatusShown(reopened, "CachesStatus"));
        Assert.True(reopened.FindControl<Border>("RecommendedSavedLine")!.IsEffectivelyVisible);
        Assert.StartsWith("Saved.", rvm2.RecommendedStatus);
        Assert.False(reopened.FindControl<Border>("RecommendedUnsavedLine")!.IsEffectivelyVisible);
        Assert.False(reopened.FindControl<StackPanel>("UnsavedFilters")!.IsEffectivelyVisible);
        // An in-place group lists the rules in effect.
        Assert.Equal(RecommendedFilters.GroupRules(created!.FiltersText, RecommendedFilters.Group.CachesAndJunk), rvm2.CachesState.Rules);
        ctx.Snapshot(reopened, "saved-backup-reopened-in-place",
            "The new backup saved and opened again: a green 'Saved. This page shows what this backup uses now; there is nothing to do here.' line, and a green 'In place' check beside each of the three groups. Nothing to Apply or Save.");

        // Untick Windows system files: its rules leave the filters at once,
        // and the page and the footer say it waits for Save.
        ClickControl(ctx, reopened, reopened.FindControl<CheckBox>("WindowsSystemGroup")!);
        Assert.False(rvm2.RecExcludeWindowsSystem);
        Assert.Equal(GroupStatus.WillBeRemoved, rvm2.WindowsSystemState.Status);
        Assert.Equal("Will be removed when you save", StatusShown(reopened, "WindowsSystemStatus"));
        Assert.Equal(GroupStatus.InPlace, rvm2.CachesState.Status); // the others don't move
        Assert.Empty(RecommendedFilters.GroupRules(rvm2.FiltersText, RecommendedFilters.Group.WindowsSystem));
        Assert.True(rvm2.FiltersUnsaved);
        Assert.True(reopened.FindControl<Border>("RecommendedUnsavedLine")!.IsEffectivelyVisible);
        Assert.True(reopened.FindControl<StackPanel>("UnsavedFilters")!.IsEffectivelyVisible);
        // Nothing reached the stored backup yet.
        Assert.True(RecommendedFilters.ReadChoice(ServiceLocator.Config.Current.Backups.Single(b => b.Id == created!.Id).FiltersText)!.WindowsSystem);
        reopened.FindControl<CheckBox>("WindowsSystemGroup")!.BringIntoView(); await ctx.Driver.PumpAsync(50);
        ctx.Snapshot(reopened, "group-unticked-will-be-removed",
            "Windows system files unticked: amber 'Will be removed when you save' beside it, 'Not saved yet…' at the top, and 'Filter changes not saved yet' beside Save. The other groups stay 'In place'.");
        // Ticking it back returns to the saved state: nothing waits any more.
        ClickControl(ctx, reopened, reopened.FindControl<CheckBox>("WindowsSystemGroup")!);
        Assert.Equal(GroupStatus.InPlace, rvm2.WindowsSystemState.Status);
        Assert.False(rvm2.FiltersUnsaved);
        ClickControl(ctx, reopened, reopened.FindControl<CheckBox>("WindowsSystemGroup")!);
        Click(ctx, reopened, "Save");
        var edited = await reopenedDone.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(edited);
        ServiceLocator.Config.SaveBackupEdit(edited!);
        Assert.False(RecommendedFilters.ReadChoice(ServiceLocator.Config.Current.Backups.Single(b => b.Id == created!.Id).FiltersText)!.WindowsSystem);

        var (again, avm, againDone) = await Reopen();
        Assert.False(avm.RecExcludeWindowsSystem);
        Assert.Equal(GroupStatus.NotUsed, avm.WindowsSystemState.Status);
        Assert.Equal(GroupStatus.InPlace, avm.CachesState.Status);
        Assert.Equal(GroupStatus.InPlace, avm.EssentialsState.Status);
        Assert.False(avm.FiltersUnsaved);
        ctx.Snapshot(again, "saved-unticked-group-persists",
            "Saved and opened again: Windows system files stays unticked and reads 'Off: these files are backed up', its list 'Its 3 rules, if ticked'; Essentials and caches read 'In place'; the top line is green again.");
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        await ctx.Driver.PumpAsync(150);
        ctx.Snapshot(again, "saved-unticked-group-persists-dark",
            "The same page in the dark theme: green 'In place' pills and the green saved line stay readable.");
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        await ctx.Driver.PumpAsync(50);
        Click(ctx, again, "Cancel");
        Assert.Null(await againDone.WaitAsync(TimeSpan.FromSeconds(5)));
        homeOwner.Close();
        // Out of the way of the card checks below.
        ServiceLocator.Config.Update(c => c.Backups.RemoveAll(b => b.Id == created!.Id));

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
        // Nothing changed yet: the caches group is in place, with suggestions waiting.
        Assert.Equal(saved, rvm.FiltersText);
        Assert.Equal(GroupStatus.InPlace, rvm.CachesState.Status);
        Assert.Equal(3, rvm.CachesState.Pending);
        Assert.False(rvm.FiltersUnsaved);
        ctx.Snapshot(reviewing, "review-panel-with-measured-effects",
            "Review opened Filters > Recommended at 'New filter suggestions': node_modules/ leaves out 2 files (41.0 KB), *.tmp 1 file (4.9 KB), *.crdownload matches nothing here today; each ticked, with Use ticked suggestions and Hide all. The page says it is saved: nothing has changed yet.");

        // Hide one, accept the others.
        reviewing.GetVisualDescendants().OfType<CheckBox>().Single(c => c.DataContext is RecommendationChangeChoice { Pattern: "*.crdownload" }).IsChecked = false;
        Click(ctx, reviewing, "Use ticked suggestions");
        var after = rvm.FiltersText.Replace("\r\n", "\n").Split('\n');
        Assert.Contains("node_modules/", after);
        Assert.Contains("*.tmp", after);
        Assert.DoesNotContain("*.crdownload", after);
        Assert.False(rvm.HasRecommendationUpdate);
        Assert.Equal("1 suggested change is hidden for this backup.", rvm.HiddenRecommendationsText);
        // The accepted suggestions wait for Save like any other change.
        Assert.Equal(GroupStatus.WillChange, rvm.CachesState.Status);
        Assert.True(rvm.FiltersUnsaved);
        Assert.Equal("Will change when you save", StatusShown(reviewing, "CachesStatus"));
        reviewing.FindControl<Border>("RecommendedCard")!.BringIntoView(); await ctx.Driver.PumpAsync(50);
        ctx.Snapshot(reviewing, "suggestions-used-waiting-for-save",
            "After Use ticked suggestions: the panel is gone, '1 suggested change is hidden for this backup.', the caches group reads 'Will change when you save', and the page and footer say the change is not saved yet.");
        // Ticking a group never brings a hidden suggestion back.
        ClickControl(ctx, reviewing, reviewing.FindControl<CheckBox>("CachesGroup")!);
        ClickControl(ctx, reviewing, reviewing.FindControl<CheckBox>("CachesGroup")!);
        Assert.True(rvm.RecExcludeCachesAndJunk);
        Assert.DoesNotContain("*.crdownload", rvm.FiltersText.Replace("\r\n", "\n").Split('\n'));
        rvm.RecExcludeWindowsSystem = true;
        Assert.True(RecommendedFilters.ReadChoice(rvm.FiltersText)!.WindowsSystem);
        rvm.RecExcludeWindowsSystem = false;
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

    /// <summary>The words of the status pill named <paramref name="name"/>.</summary>
    private static string? StatusShown(Window window, string name)
        => window.FindControl<ContentControl>(name)!.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible).Text;

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
