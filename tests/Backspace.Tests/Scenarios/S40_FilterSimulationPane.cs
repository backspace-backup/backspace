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
/// Regression for ROADMAP coverage-matrix gap "Filter simulation pane"
/// and for ROADMAP P1 "Filters > Advanced > Simulate fails with exit
/// status 1". The Advanced filter editor has a Simulate button that
/// runs the configured rules against the actual source tree and shows
/// which files would be included / excluded — the key tool for users
/// to reason about their filters BEFORE running a backup.
///
/// Cases:
///   1. Preview, no rules → every candidate included.
///   2. Preview, exclude *.tmp → those candidates excluded.
///   3. THE USER'S PATH: BackupEditorWindow → Filters → Advanced →
///      Simulate button. Runs the real local walk against a real
///      on-disk tree and asserts the pane shows ✓/✗ lines with the
///      matching rule, and that excluded subtrees are not entered.
///      (The pre-fix engine dry-run died with "exit status 1" before
///      producing a single line — Backrest's shlex tokenizer ate the
///      Windows path backslashes.)
///   4. Simulate with a nonexistent source → friendly "No files
///      found" message instead of an error or silence.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S40_FilterSimulationPane : ScenarioBase
{
    protected override string ScenarioName => "S40-filter-simulation-pane";

    [AvaloniaFact] public async Task Run_S40() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var src = Path.Combine(ctx.RootDir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "doc1.txt"), "keeper");
        File.WriteAllText(Path.Combine(src, "doc2.txt"), "keeper");
        File.WriteAllText(Path.Combine(src, "scratch.tmp"), "temp");
        File.WriteAllText(Path.Combine(src, "other.tmp"), "temp");
        File.WriteAllText(Path.Combine(src, "important.tmp"), "actually keeper");

        var sim = ServiceLocator.Filters;
        var candidates = new[]
        {
            "doc1.txt", "doc2.txt",
            "scratch.tmp", "other.tmp", "important.tmp"
        };

        // -------- Case 1: no rules → everything passes through --------
        var emptyResult = sim.Preview(filtersText: "", candidates);
        var emptyIncluded = emptyResult.Where(e => e.Included).Count();
        var emptyExcluded = emptyResult.Where(e => !e.Included).Count();
        Assert.Equal(5, emptyIncluded);
        Assert.Equal(0, emptyExcluded);
        ctx.Report.RecordNote("case 1 empty filters",
            $"Included={emptyIncluded} Excluded={emptyExcluded}");

        // -------- Case 2: exclude *.tmp removes those files --------
        var excludeResult = sim.Preview(filtersText: "*.tmp", candidates);
        var excludedCount = excludeResult.Where(e => !e.Included).Count();
        var includedCount = excludeResult.Where(e => e.Included).Count();
        Assert.Equal(3, excludedCount);
        Assert.Equal(2, includedCount);  // doc1.txt + doc2.txt
        ctx.Report.RecordNote("case 2 exclude *.tmp",
            $"Included={includedCount} Excluded={excludedCount}");

        // -------- Case 3: the Simulate BUTTON's real path --------
        // Editor window → Filters tab → Advanced sub-tab → Simulate.
        // This is the exact flow that used to die with "exit status 1".
        var simSrc = Path.Combine(ctx.RootDir, "simulate-src");
        Directory.CreateDirectory(Path.Combine(simSrc, "cache"));
        File.WriteAllText(Path.Combine(simSrc, "keep.txt"), "keeper");
        File.WriteAllText(Path.Combine(simSrc, "scratch.tmp"), "temp");
        File.WriteAllText(Path.Combine(simSrc, "cache", "junk.bin"), "junk");

        var backup = new Backup
        {
            Name = "sim-pane",
            SourcePaths = { simSrc },
            FiltersText = "*.tmp\ncache/",
        };
        var vm = new BackupEditorViewModel(backup, isNew: false);
        var win = new BackupEditorWindow { DataContext = vm, Width = 980, Height = 820 };
        ctx.Driver.Show(win);

        // Outer tabs: Overview=0, Schedule=1, Filters=2. Inner Filters
        // sub-tabs: Recommended=0, Advanced=1.
        var outerTabs = win.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        Assert.NotNull(outerTabs);
        outerTabs!.SelectedIndex = 2;
        ctx.Driver.Pump();
        var innerTabs = win.GetVisualDescendants().OfType<TabControl>()
            .FirstOrDefault(t => !ReferenceEquals(t, outerTabs));
        Assert.NotNull(innerTabs);
        innerTabs!.SelectedIndex = 1;
        ctx.Driver.Pump();

        await vm.RunFilterSimulationCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        ctx.Driver.Pump();

        var log = vm.FilterSimulationLog;
        Assert.Contains("✓", log);
        Assert.Contains("keep.txt", log);
        Assert.Contains("✗", log);
        Assert.Contains("scratch.tmp", log);
        Assert.Contains("(rule: *.tmp)", log);          // the pane names the matching rule
        Assert.Contains("(rule: cache/)", log);         // excluded dir reported once...
        Assert.DoesNotContain("junk.bin", log);         // ...and its subtree never entered
        Assert.DoesNotContain("[error]", log);

        ctx.Snapshot(win, "simulate pane populated",
            "Filters > Advanced after clicking Simulate: right pane lists ✓ keep.txt " +
            "and ✗ scratch.tmp / ✗ cache with the matching rule per exclusion. " +
            "Before the fix this pane showed only \"[error] ... exit status 1\".");

        ctx.Report.RecordNote("case 3 simulate button",
            $"Pane content after Simulate:\n{log}");
        win.Close();

        // -------- Case 4: nonexistent source → friendly message --------
        var ghost = new Backup
        {
            Name = "sim-ghost",
            SourcePaths = { Path.Combine(ctx.RootDir, "does-not-exist") },
            FiltersText = "*.tmp",
        };
        var ghostVm = new BackupEditorViewModel(ghost, isNew: false);
        await ghostVm.RunFilterSimulationCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Contains("No files found", ghostVm.FilterSimulationLog);
        Assert.DoesNotContain("[error]", ghostVm.FilterSimulationLog);
        ctx.Report.RecordNote("case 4 missing source",
            $"Pane content: {ghostVm.FilterSimulationLog.Trim()}");

        await Task.Yield();
    }
}
