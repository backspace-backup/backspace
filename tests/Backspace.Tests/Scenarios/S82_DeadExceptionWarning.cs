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
/// U127 (4 November 2026): an exception such as <c>!cache/keep/</c> under a
/// folder excluded whole by <c>cache/</c> keeps nothing, because the engine
/// never goes into <c>cache</c>. The filter editor now says so, and the
/// warning goes away once the rule excludes the folder's contents instead.
/// The Simulate pane agrees: the "kept" file is not in the backup.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S82_DeadExceptionWarning : ScenarioBase
{
    protected override string ScenarioName => "S82-dead-exception-warning";

    [AvaloniaFact] public async Task Run_S82() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var source = Path.Combine(ctx.RootDir, "project");
        Directory.CreateDirectory(Path.Combine(source, "cache", "keep"));
        File.WriteAllText(Path.Combine(source, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(source, "cache", "junk.bin"), "junk");
        File.WriteAllText(Path.Combine(source, "cache", "keep", "settings.json"), "{}");
        var backup = new Backup { Name = "Project", SourcePaths = { source }, FiltersText = "cache/\n!cache/keep/" };
        var vm = new BackupEditorViewModel(backup, isNew: false);
        var win = new BackupEditorWindow { DataContext = vm, Width = 980, Height = 820 };
        ctx.Driver.Show(win);
        var outer = win.GetVisualDescendants().OfType<TabControl>().First();
        outer.SelectedIndex = 2;
        ctx.Driver.Pump();
        var inner = win.GetVisualDescendants().OfType<TabControl>().First(t => !ReferenceEquals(t, outer));
        inner.SelectedIndex = 1;
        ctx.Driver.Pump();

        var warning = win.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "DeadExceptionWarning");
        Assert.True(warning.IsEffectivelyVisible);
        Assert.StartsWith("!cache/keep/ can never apply: the backup never looks inside cache, which cache/ excludes.", vm.FilterExceptionWarning);
        await vm.RunFilterSimulationCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ctx.Driver.Pump();
        Assert.DoesNotContain("settings.json", vm.FilterSimulationLog);
        ctx.Snapshot(win, "exception that keeps nothing",
            "Rules cache/ then !cache/keep/: the editor warns that the exception keeps nothing and how to fix it; Simulate shows cache excluded and settings.json absent.");

        // Excluding the folder's contents instead makes the exception work.
        vm.FiltersText = "cache/*\n!cache/keep/";
        ctx.Driver.Pump();
        Assert.Null(vm.FilterExceptionWarning);
        Assert.False(warning.IsEffectivelyVisible);
        await vm.RunFilterSimulationCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ctx.Driver.Pump();
        Assert.Contains("settings.json", vm.FilterSimulationLog);
        Assert.DoesNotContain("junk.bin", vm.FilterSimulationLog.Split('\n').Where(l => l.Contains('✓')).Aggregate("", (a, l) => a + l));
        ctx.Snapshot(win, "fixed rule keeps the subfolder",
            "Rules cache/* then !cache/keep/: no warning, and Simulate shows settings.json kept while junk.bin stays excluded.");
        win.Close();
    }
}
