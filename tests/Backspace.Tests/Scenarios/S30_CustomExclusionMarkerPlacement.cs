using System;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// Regression for ROADMAP P2 "Custom-exclusion markers should sit
/// BEFORE Backspace-generated block." The integration test asserts
/// that <see cref="RecommendedFilters.MergeIntoFilters"/> produces a
/// file whose layout matches the user's mental model: edit zone at
/// the top, Backspace-generated block parked below.
///
/// Unit tests proved the algorithm; this proves the layout users
/// will actually see when they open the filter editor on a real
/// pre-existing filter file.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S30_CustomExclusionMarkerPlacement : ScenarioBase
{
    protected override string ScenarioName => "S30-custom-marker-placement";

    [AvaloniaFact] public async Task Run_S30() => await Run();

    protected override Task RunAsync(ScenarioContext ctx)
    {
        // -------- Case 1: brand-new (no markers present) --------
        var generated = RecommendedFilters.RenderBlock(
            includeWindowsSystem: true,
            includeCachesAndJunk: true,
            cloudExcludes: System.Array.Empty<RecommendedFilters.CloudSyncFolder>());

        var emptyMerged = RecommendedFilters.MergeIntoFilters(existingFilters: "", generated);

        var customStart = emptyMerged.IndexOf(RecommendedFilters.CustomBlockHeader, StringComparison.Ordinal);
        var customEnd = emptyMerged.IndexOf(RecommendedFilters.CustomBlockFooter, customStart, StringComparison.Ordinal);
        var autoStart = emptyMerged.IndexOf(RecommendedFilters.AutoBlockHeader, StringComparison.Ordinal);

        Assert.True(customStart >= 0, "Custom block header must be present.");
        Assert.True(customEnd > customStart, "Custom block footer must follow header.");
        Assert.True(autoStart > customEnd,
            "Backspace-generated block must come AFTER the custom block, not before. " +
            $"customStart={customStart}, customEnd={customEnd}, autoStart={autoStart}.");

        ctx.Report.RecordNote(
            "empty merge layout",
            "Custom block at top; Backspace-generated block parks below it.");

        // -------- Case 2: existing loose user rules migrate INTO the custom block --------
        // A user with a hand-edited filter file (pre-feature) lands
        // their rules at the top of the file. The merger should pull
        // them INTO the custom block so the user's edit zone is the
        // single canonical place.
        var legacy = "# old hand-edited rules\n*.bak\n# another comment\n*.tmp\n";
        var legacyMerged = RecommendedFilters.MergeIntoFilters(legacy, generated);

        var lCustomStart = legacyMerged.IndexOf(RecommendedFilters.CustomBlockHeader, StringComparison.Ordinal);
        var lCustomEnd = legacyMerged.IndexOf(RecommendedFilters.CustomBlockFooter, lCustomStart, StringComparison.Ordinal);
        var lCustomBody = legacyMerged.Substring(lCustomStart, lCustomEnd - lCustomStart);

        Assert.Contains("*.bak", lCustomBody);
        Assert.Contains("*.tmp", lCustomBody);
        Assert.Contains("# old hand-edited rules", lCustomBody);

        ctx.Report.RecordNote(
            "legacy migration",
            "Pre-existing loose user rules migrate INTO the custom block on first re-apply.");

        // -------- Case 3: idempotent on a second apply --------
        // Re-running the same merge against the already-merged text
        // should produce IDENTICAL output (no drift, no double markers).
        var secondMerge = RecommendedFilters.MergeIntoFilters(legacyMerged, generated);
        Assert.Equal(legacyMerged, secondMerge);

        ctx.Report.RecordNote(
            "idempotent",
            "Re-applying the same merge against already-merged text leaves it unchanged.");

        return Task.CompletedTask;
    }
}
