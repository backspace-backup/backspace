using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U150 (5 November 2026): the backup settings window had tab headers larger
/// than its title and wide margins; it is now laid out like the main window.
/// U149: the Recommended tab's figure counts only the ticked recommendations,
/// and the Advanced tab counts what all the rules leave out. U141: a
/// recommended rule the person removed is offered again and can be hidden.
/// U148: rules that can't match in the backup's folders are not offered.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S88_BackupSettingsLayoutAndRelevantRules : ScenarioBase
{
    protected override string ScenarioName => "S88-backup-settings-layout-and-relevant-rules";

    [AvaloniaFact] public async Task Run_S88() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        await Layout(ctx);
        await RemovedRuleOfferedAgain(ctx);
        RulesOutsideTheFoldersAreNotOffered(ctx);
    }

    private static Button? Button(Window win, string text) => win.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(b => b.Content as string == text);

    private static TabControl Tabs(Window win) => win.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("editor-tabs"));

    private static async Task Layout(ScenarioContext ctx)
    {
        var source = ctx.MakeSource("profile", targetBytes: 20_000);
        Directory.CreateDirectory(Path.Combine(source, "node_modules"));
        File.WriteAllBytes(Path.Combine(source, "node_modules", "scratch.bin"), new byte[3_000_000]);
        Directory.CreateDirectory(Path.Combine(source, "Videos"));
        File.WriteAllBytes(Path.Combine(source, "Videos", "big.mp4"), new byte[2_000_000]);
        var backup = new Backup
        {
            Name = "local_c",
            FiltersText = RecommendedFilters.MergeIntoFilters("Videos/\n",
                RecommendedFilters.RenderBlock(true, true, Array.Empty<RecommendedFilters.CloudSyncFolder>(), new[] { source })),
        };
        backup.SourcePaths.Add(source);
        var vm = new BackupEditorViewModel(backup, isNew: false);
        var win = new BackupEditorWindow { DataContext = vm, Width = 1000, Height = 840 };
        ctx.Driver.Show(win);
        Assert.True(ctx.Driver.WaitFor(() => win.GetVisualDescendants().OfType<TabControl>().Any()));
        await ctx.Driver.PumpAsync(200);

        // The tab headers are no larger than a section title.
        var header = win.GetVisualDescendants().OfType<TabItem>().First(t => t.Header as string == "Overview");
        Assert.True(header.FontSize <= 16, $"Tab header font {header.FontSize}");
        Assert.True(header.Bounds.Height <= 40, $"Tab strip height {header.Bounds.Height}");
        ctx.Snapshot(win, "overview",
            "Edit «local_c», Overview tab: tab names the size of a section title on a 38 px strip, cards closer together, less margin around the window.");

        Tabs(win).SelectedIndex = 1;
        await ctx.Driver.PumpAsync(200);
        ctx.Snapshot(win, "schedule", "Schedule tab with the same compact header.");

        // Recommended: the figure counts the ticked recommendations only.
        Tabs(win).SelectedIndex = 2;
        await ctx.Driver.PumpAsync(200);
        Assert.Equal(GroupStatus.InPlace, vm.CachesState.Status);
        Button(win, "Estimate savings")!.Command!.Execute(null);
        Assert.True(ctx.Driver.WaitFor(() => vm.RecEstimatedSavingsBytes >= 0, 20_000), "The estimate never finished.");
        await ctx.Driver.PumpAsync(100);
        // node_modules (3 MB) is a recommended rule; Videos (2 MB) is the person's own.
        Assert.InRange(vm.RecEstimatedSavingsBytes, 3_000_000, 4_000_000);
        Assert.StartsWith("The ticked recommended exclusions leave out 2.9 MB", vm.RecEstimatedSavingsLabel);
        ctx.Snapshot(win, "recommended",
            "Filters → Recommended: 'The ticked recommended exclusions leave out 2.9 MB of this backup's folders.' (node_modules only; the person's own Videos rule is not counted here).");

        // Advanced: Count measures all the rules, the person's own included.
        var sub = win.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("sub"));
        sub.SelectedIndex = 1;
        await ctx.Driver.PumpAsync(200);
        Assert.StartsWith("Not counted yet", vm.AllRulesSavingsLabel);
        Button(win, "Count")!.Command!.Execute(null);
        Assert.True(ctx.Driver.WaitFor(() => vm.AllRulesSavingsBytes >= 0, 20_000), "The count never finished.");
        await ctx.Driver.PumpAsync(100);
        Assert.InRange(vm.AllRulesSavingsBytes, 5_000_000, 6_000_000);
        Assert.StartsWith("All these rules leave out 4.8 MB", vm.AllRulesSavingsLabel);
        ctx.Snapshot(win, "advanced",
            "Filters → Advanced: one short syntax line, Simulate and Count on one row with 'All these rules leave out 4.8 MB of this backup's folders.', and two one-line pane headers above the rules and the simulation.");
        // Editing the rules makes the count out of date.
        vm.FiltersText += "\n*.log";
        Assert.Equal(-1, vm.AllRulesSavingsBytes);
        win.Close();
    }

    private static async Task RemovedRuleOfferedAgain(ScenarioContext ctx)
    {
        var source = ctx.MakeSource("profile2", targetBytes: 4_000);
        var backup = new Backup
        {
            Name = "local_c",
            FiltersText = RecommendedFilters.RenderBlock(false, true, Array.Empty<RecommendedFilters.CloudSyncFolder>(), new[] { source }),
        };
        backup.SourcePaths.Add(source);
        var vm = new BackupEditorViewModel(backup, isNew: false);
        Assert.False(vm.HasRecommendationUpdate);

        // The person deletes one recommended line on the Advanced tab.
        vm.FiltersText = string.Join("\n", vm.FiltersText!.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim() != "node_modules/"));
        Assert.Contains(vm.RecommendedAdditions, c => c.Pattern == "node_modules/");
        Assert.Equal(GroupStatus.WillChange, vm.CachesState.Status); Assert.True(vm.FiltersUnsaved);

        var win = new BackupEditorWindow { DataContext = vm, Width = 1000, Height = 840 };
        ctx.Driver.Show(win);
        Assert.True(ctx.Driver.WaitFor(() => win.GetVisualDescendants().OfType<TabControl>().Any()));
        Tabs(win).SelectedIndex = 2;
        await ctx.Driver.PumpAsync(200);
        ctx.Snapshot(win, "removed rule offered again",
            "After deleting 'node_modules/' from the recommended block, Filters → Recommended offers it again under 'Updated recommendations', ticked, with 'Use ticked suggestions' and 'Hide all'.");

        // Unticked and applied: hidden for this backup, and listed as hidden.
        vm.RecommendedAdditions.Single(c => c.Pattern == "node_modules/").IsSelected = false;
        Button(win, "Use ticked suggestions")!.Command!.Execute(null);
        await ctx.Driver.PumpAsync(200);
        Assert.False(vm.HasRecommendationUpdate);
        Assert.True(vm.HasHiddenRecommendations);
        Assert.DoesNotContain("node_modules/", vm.FiltersText!.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()));
        ctx.Snapshot(win, "declined rule hidden",
            "Unticked and used: the offer is gone, '1 suggested change is hidden for this backup.' with 'Show hidden suggestions' to bring it back.");
        win.Close();
    }

    private static void RulesOutsideTheFoldersAreNotOffered(ScenarioContext ctx)
    {
        // A backup of a Dropbox folder, not a whole drive and not a profile.
        // Only the paths matter here (the test folders all sit under a profile).
        const string dropbox = @"D:\Dropbox";
        var block = RecommendedFilters.RenderBlock(true, true, Array.Empty<RecommendedFilters.CloudSyncFolder>(), new[] { dropbox });
        var lines = block.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        Assert.DoesNotContain("?:/Windows/", lines);
        Assert.DoesNotContain("?:/pagefile.sys", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("?:/Users/", StringComparison.Ordinal));
        Assert.Contains("node_modules/", lines);
        Assert.Contains("thumbs.db", lines);
        // A Dropbox folder inside a profile: the profile rules (?:/Users/*/AppData/…)
        // lead elsewhere in the profile, so they are not offered either (U151).
        var inProfile = RecommendedFilters.RenderBlock(true, true, Array.Empty<RecommendedFilters.CloudSyncFolder>(), new[] { @"C:\Users\me\Dropbox" });
        var profileLines = inProfile.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        Assert.DoesNotContain(profileLines, l => l.StartsWith("?:/Users/", StringComparison.Ordinal));
        Assert.Contains("node_modules/", profileLines);

        // An existing backup of that folder with the full lists is offered
        // their removal; widening it to a whole drive offers them back.
        var full = RecommendedFilters.RenderBlock(true, true, Array.Empty<RecommendedFilters.CloudSyncFolder>());
        var update = RecommendedFilters.CheckForUpdates(full, sources: new[] { dropbox });
        Assert.Contains("?:/Windows/", update.Remove);
        Assert.Contains("?:/Users/*/AppData/Local/Temp/", update.Remove);
        Assert.DoesNotContain("node_modules/", update.Remove);
        Assert.Empty(update.Add);
        var trimmed = RecommendedFilters.ApplyUpdates(full, update);
        Assert.True(RecommendedFilters.CheckForUpdates(trimmed, sources: new[] { dropbox }).IsEmpty);
        const string drive = @"D:\";
        Assert.Contains("?:/Windows/", RecommendedFilters.CheckForUpdates(trimmed, sources: new[] { dropbox, drive }).Add);
        ctx.Report.RecordNote("Recommended block for a Dropbox folder", block);
    }
}
