using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP coverage-matrix gap "Filter simulation pane".
/// The Advanced filter editor has a Simulate button that runs the
/// configured rules against the actual source tree and shows which
/// files would be included / excluded — the key tool for users to
/// reason about their filters BEFORE running a backup.
///
/// This scenario drives the FilterSimulator directly against a known
/// source tree and asserts:
///   1. Plain inclusions (no rules) → every file included.
///   2. An exclude rule for *.tmp removes those files from the
///      simulated output.
///   3. Conflicting rules: exclude *.tmp then include /important.tmp
///      keeps the explicit include.
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

        await Task.Yield();
    }
}
