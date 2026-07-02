using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP P1 "Estimated savings shows 0 B". Asserts
/// each user-visible message variant the BackupEditorViewModel
/// renders end-to-end, after a real SavingsEstimator walk against a
/// real on-disk source — not just the unit-test layer's label
/// fixture.
///
///   1. Source contains files but NONE match the recommended rules
///      → "Nothing in your sources matched…" friendly message.
///   2. Source contains zero files at all → "Couldn't read any
///      files…" diagnostic message.
///   3. Source contains a generated cache subdirectory matching a
///      recommended rule → positive bytes label with a real number.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S34_SavingsLabelStates : ScenarioBase
{
    protected override string ScenarioName => "S34-savings-label-states";

    [AvaloniaFact] public async Task Run_S34() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var estimator = ServiceLocator.Savings;
        var filtersText = RecommendedFilters.RenderBlock(
            includeWindowsSystem: true,
            includeCachesAndJunk: true,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());

        // -------- Case 1: narrow source (only Documents-style files) --------
        var narrow = Path.Combine(ctx.RootDir, "narrow");
        Directory.CreateDirectory(narrow);
        File.WriteAllText(Path.Combine(narrow, "letter.txt"), "Dear Sir/Madam,\n…");
        File.WriteAllText(Path.Combine(narrow, "notes.md"), "# Notes\n…");

        var narrowEstimate = await estimator.ComputeAsync(new[] { narrow }, filtersText, default);
        var vm = new BackupEditorViewModel(new Backup(), isNew: true);
        vm.RecEstimatedSavingsBytes = narrowEstimate.ExcludedBytes;
        vm.RecScannedBytes = narrowEstimate.ExcludedBytes + narrowEstimate.IncludedBytes;
        vm.RecEstimatedTruncated = narrowEstimate.Truncated;

        Assert.Equal(0, narrowEstimate.ExcludedBytes);
        Assert.True(narrowEstimate.IncludedBytes > 0);
        Assert.Contains("Nothing in your sources matched", vm.RecEstimatedSavingsLabel);

        ctx.Report.RecordNote(
            "case 1 narrow source",
            $"Excluded={narrowEstimate.ExcludedBytes} Included={narrowEstimate.IncludedBytes}. " +
            $"Label: {vm.RecEstimatedSavingsLabel}");

        // -------- Case 2: empty source --------
        var empty = Path.Combine(ctx.RootDir, "empty");
        Directory.CreateDirectory(empty);

        var emptyEstimate = await estimator.ComputeAsync(new[] { empty }, filtersText, default);
        vm.RecEstimatedSavingsBytes = emptyEstimate.ExcludedBytes;
        vm.RecScannedBytes = emptyEstimate.ExcludedBytes + emptyEstimate.IncludedBytes;
        vm.RecEstimatedTruncated = emptyEstimate.Truncated;

        Assert.Equal(0, emptyEstimate.ExcludedBytes);
        Assert.Equal(0, emptyEstimate.IncludedBytes);
        Assert.Contains("Couldn't read any files", vm.RecEstimatedSavingsLabel);

        ctx.Report.RecordNote(
            "case 2 empty source",
            $"Label: {vm.RecEstimatedSavingsLabel}");

        // -------- Case 3: source with files matching a recommended rule --------
        // Emit a "Temp" subdirectory — should match cache exclusions.
        var matched = Path.Combine(ctx.RootDir, "matched");
        Directory.CreateDirectory(matched);
        File.WriteAllText(Path.Combine(matched, "keeper.txt"), "important");
        var cacheDir = Path.Combine(matched, "Temp");
        Directory.CreateDirectory(cacheDir);
        var bigBytes = new byte[256_000];
        new Random(42).NextBytes(bigBytes);
        File.WriteAllBytes(Path.Combine(cacheDir, "huge-cache-file.dat"), bigBytes);

        var matchedEstimate = await estimator.ComputeAsync(new[] { matched }, filtersText, default);
        vm.RecEstimatedSavingsBytes = matchedEstimate.ExcludedBytes;
        vm.RecScannedBytes = matchedEstimate.ExcludedBytes + matchedEstimate.IncludedBytes;
        vm.RecEstimatedTruncated = matchedEstimate.Truncated;

        Assert.True(matchedEstimate.ExcludedBytes > 0,
            $"Source with Temp/ subdir should match a recommended exclusion. " +
            $"Got excluded={matchedEstimate.ExcludedBytes}.");
        Assert.Contains("Estimated savings", vm.RecEstimatedSavingsLabel);

        ctx.Report.RecordNote(
            "case 3 matched source",
            $"Excluded={matchedEstimate.ExcludedBytes} bytes. Label: {vm.RecEstimatedSavingsLabel}");
    }
}
