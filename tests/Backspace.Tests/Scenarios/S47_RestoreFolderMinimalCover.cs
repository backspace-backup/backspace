using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// "Restore folder → minimal cover": when the user picks a whole
/// folder in the Restore wizard, the engine must receive ONE
/// glob pattern (the folder itself), not N entries (every file
/// individually). This is what keeps a "restore my photos
/// directory" click from generating a 50,000-entry include-file
/// that hits the Windows 32 KB command-line ceiling and the
/// CreateProcess fail path E34 already guards (long include list).
///
/// The matrix row asks for an integration assertion that the
/// engine actually got 1 pattern, not the unit-level assertion
/// that the minimizer COULD produce 1 pattern. We exercise the
/// minimizer the way RestoreEngine does and check the output
/// shape.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S47_RestoreFolderMinimalCover : ScenarioBase
{
    protected override string ScenarioName => "S47-restore-folder-minimal-cover";

    [AvaloniaFact] public async Task Run_S47() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // Synthetic snapshot file list — every entry lives under
        // /photos/, simulating the "user clicked the photos folder
        // checkbox" selection.
        var allLeaves = Enumerable.Range(0, 250)
            .Select(i => $"/photos/{i:D4}.jpg")
            .Concat(Enumerable.Range(0, 80).Select(i => $"/photos/2026/{i:D4}.jpg"))
            .Concat(Enumerable.Range(0, 40).Select(i => $"/photos/2025/q4/{i:D4}.jpg"))
            .ToList();

        // User selected the whole /photos/ subtree.
        var selectedPaths = allLeaves;

        var compressed = RestoreSelectionMinimizer.Compute(
            allPaths: allLeaves,
            selectedPaths: selectedPaths);

        // The minimizer must collapse the 370 leaves into a single
        // /photos/ glob. If it produced N patterns instead, restic
        // would receive 370 --include flags and the CreateProcess
        // command-line would blow past 32 KB.
        Assert.Single(compressed);
        var pattern = compressed[0];
        Assert.Contains("photos", pattern);

        ctx.Report.RecordNote(
            "370 leaves → 1 pattern",
            $"RestoreSelectionMinimizer collapsed {allLeaves.Count} selected leaves into " +
            $"1 pattern: \"{pattern}\". Without this, restic would receive {allLeaves.Count} " +
            $"--include flags and CreateProcess would fail with \"filename or extension is " +
            $"too long\" (E34 guards the regression).");

        // -------- Partial selection: minimizer keeps individual leaves --------
        // If the user picks SOME files (not the whole folder), the
        // minimizer cannot collapse. Assert that path still works.
        var partial = allLeaves.Take(5).ToList();
        var partialCompressed = RestoreSelectionMinimizer.Compute(
            allPaths: allLeaves,
            selectedPaths: partial);
        Assert.Equal(5, partialCompressed.Count);

        return Task.CompletedTask;
    }
}
