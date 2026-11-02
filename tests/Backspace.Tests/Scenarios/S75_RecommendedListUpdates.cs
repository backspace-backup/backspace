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
/// U057: when Backspace's recommended exclusions change, a saved backup keeps
/// its rules. Opening it in the editor lists each addition and removal for the
/// groups it uses with its own checkbox; "Apply ticked changes" applies only the
/// ticked ones and remembers the unticked ones with the backup, and
/// "Don't suggest these again" declines them all.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S75_RecommendedListUpdates : ScenarioBase
{
    protected override string ScenarioName => "S75-recommended-list-updates";

    [AvaloniaFact] public async Task Run_S75() => await Run();

    private const string DeclinedAddition = "node_modules/";
    private const string DeclinedRemoval = "Users/*/AppData/Local/Packages/";

    private static string SavedByEarlierVersion() => string.Join("\n",
        RecommendedFilters.CustomBlockHeader, "Projects/build/", RecommendedFilters.CustomBlockFooter, "",
        RecommendedFilters.AutoBlockHeader,
        "# Essentials: always excluded; safe for any source.", "/pagefile.sys", "Recovery/", "",
        "# App caches, temp files, dev junk: universally safe to skip.", "temp/", DeclinedRemoval, "",
        RecommendedFilters.AutoBlockFooter);

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var backup = new Backup { Name = "saved-earlier", FiltersText = SavedByEarlierVersion() };
        backup.SourcePaths.Add(System.IO.Path.Combine(ctx.RootDir, "src"));
        var vm = new BackupEditorViewModel(backup, isNew: false);
        Assert.True(vm.HasRecommendationUpdate);
        Assert.Contains(vm.RecommendedAdditions, c => c.Pattern == "/Recovery/");
        Assert.Contains(vm.RecommendedAdditions, c => c.Pattern == DeclinedAddition);
        Assert.Contains(vm.RecommendedRemovals, c => c.Pattern == "Recovery/");
        Assert.Contains(vm.RecommendedRemovals, c => c.Pattern == DeclinedRemoval);
        Assert.All(vm.RecommendedAdditions.Concat(vm.RecommendedRemovals), c => Assert.True(c.IsSelected));

        var win = new BackupEditorWindow { DataContext = vm, Width = 1000, Height = 860 };
        ctx.Driver.Show(win);
        Assert.True(ctx.Driver.WaitFor(() => win.GetVisualDescendants().OfType<TabControl>().Any()));
        win.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
        ctx.Driver.Pump();
        Assert.True(ctx.Driver.WaitFor(() => Button(win, "Apply ticked changes") is { IsEffectivelyVisible: true }),
            "The update offer never appeared for a backup saved by an earlier version.");
        // The saved rules are untouched until the person chooses.
        Assert.Equal(SavedByEarlierVersion(), vm.FiltersText);
        ctx.Snapshot(win, "update offered while editing",
            "Filters > Recommended: 'Updated recommendations' lists each addition (such as /Recovery/ and node_modules/) and each removal (Recovery/ and the old Packages rule) with its own ticked checkbox. The saved rules are unchanged.");

        // Untick one addition and one removal through their checkboxes.
        CheckBoxFor(win, DeclinedAddition).IsChecked = false;
        CheckBoxFor(win, DeclinedRemoval).IsChecked = false;
        CheckBoxFor(win, DeclinedAddition).BringIntoView();
        ctx.Driver.Pump();
        Assert.False(vm.RecommendedAdditions.Single(c => c.Pattern == DeclinedAddition).IsSelected);
        Assert.False(vm.RecommendedRemovals.Single(c => c.Pattern == DeclinedRemoval).IsSelected);
        ctx.Snapshot(win, "two changes unticked",
            "node_modules/ (an addition) and Users/*/AppData/Local/Packages/ (a removal) are unticked; every other change stays ticked.");

        Button(win, "Apply ticked changes")!.Command!.Execute(null);
        ctx.Driver.Pump();
        var lines = vm.FiltersText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        // Ticked changes applied.
        Assert.Contains("/Recovery/", lines);
        Assert.DoesNotContain("Recovery/", lines);
        // Unticked changes not applied.
        Assert.DoesNotContain(DeclinedAddition, lines);
        Assert.Contains(DeclinedRemoval, lines);
        Assert.Contains("Projects/build/", lines);
        // Unticked changes are not offered again.
        Assert.False(vm.HasRecommendationUpdate);
        Assert.True(ctx.Driver.WaitFor(() => Button(win, "Apply ticked changes") is not { IsEffectivelyVisible: true }));
        ctx.Snapshot(win, "updated", "After applying, the card is gone: the ticked changes are in the rules, the two unticked ones are not, and the person's own rule stays in the custom block.");
        var saved = vm.CommitToWorking();
        Assert.NotNull(saved);
        Assert.Contains(DeclinedAddition, saved!.IgnoredRecommendationChanges);
        Assert.Contains(DeclinedRemoval, saved.IgnoredRecommendationChanges);
        Assert.DoesNotContain("/Recovery/", saved.IgnoredRecommendationChanges);

        // Reopening the saved backup offers nothing: the declined ones are remembered,
        // but the editor says they are hidden.
        var reopened = new BackupEditorViewModel(saved, isNew: false);
        Assert.False(reopened.HasRecommendationUpdate);
        Assert.True(reopened.HasHiddenRecommendations);
        Assert.Equal("2 suggested changes are hidden for this backup.", reopened.HiddenRecommendationsText);

        // U067: the declined suggestions can be shown again, unticked.
        Assert.True(ctx.Driver.WaitFor(() => Button(win, "Show hidden suggestions") is { IsEffectivelyVisible: true }),
            "Nothing offered to show the declined suggestions again.");
        ctx.Snapshot(win, "declined suggestions hidden", "Below the recommended exclusions: '2 suggested changes are hidden for this backup.' with Show hidden suggestions.");
        Button(win, "Show hidden suggestions")!.Command!.Execute(null);
        ctx.Driver.Pump();
        Assert.True(ctx.Driver.WaitFor(() => Button(win, "Apply ticked changes") is { IsEffectivelyVisible: true }));
        Assert.Equal(new[] { DeclinedAddition }, vm.RecommendedAdditions.Select(c => c.Pattern));
        Assert.Equal(new[] { DeclinedRemoval }, vm.RecommendedRemovals.Select(c => c.Pattern));
        Assert.All(vm.RecommendedAdditions.Concat(vm.RecommendedRemovals), c => Assert.False(c.IsSelected));
        Assert.False(vm.HasHiddenRecommendations);
        CheckBoxFor(win, DeclinedAddition).BringIntoView();
        ctx.Driver.Pump();
        ctx.Snapshot(win, "hidden suggestions shown again", "The two declined changes are listed again, unticked; nothing changes until Apply ticked changes.");

        // Taking one of them now; the other stays declined.
        CheckBoxFor(win, DeclinedAddition).IsChecked = true;
        ctx.Driver.Pump();
        Button(win, "Apply ticked changes")!.Command!.Execute(null);
        ctx.Driver.Pump();
        lines = vm.FiltersText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains(DeclinedAddition, lines);
        Assert.Contains(DeclinedRemoval, lines);
        Assert.False(vm.HasRecommendationUpdate);
        Assert.Equal("1 suggested change is hidden for this backup.", vm.HiddenRecommendationsText);
        saved = vm.CommitToWorking()!;
        Assert.DoesNotContain(DeclinedAddition, saved.IgnoredRecommendationChanges);
        Assert.Contains(DeclinedRemoval, saved.IgnoredRecommendationChanges);
        win.Close();

        // Dismissing everything is remembered with the backup too.
        var other = new Backup { Name = "keeps-old", FiltersText = SavedByEarlierVersion() };
        other.SourcePaths.Add(System.IO.Path.Combine(ctx.RootDir, "src"));
        var second = new BackupEditorViewModel(other, isNew: false);
        second.IgnoreRecommendationUpdateCommand.Execute(null);
        Assert.False(second.HasRecommendationUpdate);
        Assert.Equal(SavedByEarlierVersion(), second.FiltersText);
        return Task.CompletedTask;
    }

    private static Button? Button(Window win, string text) =>
        win.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == text);

    private static CheckBox CheckBoxFor(Window win, string pattern) =>
        win.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Content is TextBlock { Text: var t } && t == pattern);
}
