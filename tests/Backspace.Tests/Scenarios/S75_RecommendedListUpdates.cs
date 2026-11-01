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
/// its rules. Opening it in the editor offers the additions and removals for
/// the groups it uses; Update applies them through the real button, and
/// "Don't suggest these again" is remembered with the backup.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S75_RecommendedListUpdates : ScenarioBase
{
    protected override string ScenarioName => "S75-recommended-list-updates";

    [AvaloniaFact] public async Task Run_S75() => await Run();

    private static string SavedByEarlierVersion() => string.Join("\n",
        RecommendedFilters.CustomBlockHeader, "Projects/build/", RecommendedFilters.CustomBlockFooter, "",
        RecommendedFilters.AutoBlockHeader,
        "# Essentials: always excluded; safe for any source.", "/pagefile.sys", "Recovery/", "",
        "# App caches, temp files, dev junk: universally safe to skip.", "temp/", "Users/*/AppData/Local/Packages/", "",
        RecommendedFilters.AutoBlockFooter);

    protected override Task RunAsync(ScenarioContext ctx)
    {
        var backup = new Backup { Name = "saved-earlier", FiltersText = SavedByEarlierVersion() };
        backup.SourcePaths.Add(System.IO.Path.Combine(ctx.RootDir, "src"));
        var vm = new BackupEditorViewModel(backup, isNew: false);
        Assert.True(vm.HasRecommendationUpdate);
        Assert.Contains("Recovery/", vm.RecommendationUpdateText);

        var win = new BackupEditorWindow { DataContext = vm, Width = 1000, Height = 860 };
        ctx.Driver.Show(win);
        Assert.True(ctx.Driver.WaitFor(() => win.GetVisualDescendants().OfType<TabControl>().Any()));
        win.GetVisualDescendants().OfType<TabControl>().First().SelectedIndex = 2;
        ctx.Driver.Pump();
        Assert.True(ctx.Driver.WaitFor(() => Button(win, "Update recommended exclusions") is { IsEffectivelyVisible: true }),
            "The update offer never appeared for a backup saved by an earlier version.");
        // The saved rules are untouched until the person chooses.
        Assert.Equal(SavedByEarlierVersion(), vm.FiltersText);
        ctx.Snapshot(win, "update offered while editing",
            "Filters > Recommended: 'Updated recommendations' lists additions (such as /Recovery/ and node_modules/) and the removal of Recovery/ and the old Packages rule, with Update and Don't suggest buttons. The saved rules are unchanged.");

        Button(win, "Update recommended exclusions")!.Command!.Execute(null);
        ctx.Driver.Pump();
        var lines = vm.FiltersText.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains("/Recovery/", lines);
        Assert.DoesNotContain("Recovery/", lines);
        Assert.Contains("Projects/build/", lines);
        Assert.False(vm.HasRecommendationUpdate);
        Assert.True(ctx.Driver.WaitFor(() => Button(win, "Update recommended exclusions") is not { IsEffectivelyVisible: true }));
        ctx.Snapshot(win, "updated", "After Update the card is gone; the person's own rule stays in the custom block.");
        win.Close();

        // Dismissing is remembered with the backup when it is saved.
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
}
