using System.Collections.Generic;
using System.Linq;
using Backspace.Services.WebDav;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U166 (owner, 8 November 2026): "When mounting, any way to cheaply estimate
/// progress so the bar can show a % instead of just looping?" The files read
/// so far over each revision's file count from restic's snapshot summary,
/// weighted by file count over several revisions; never backwards, and 99% at
/// most until the last tree has been read.
/// </summary>
public sealed class MountReadEstimateTests
{
    private static MountReadEstimate Estimate(params (string Source, int Files)[] revisions) => new(revisions);

    [Fact]
    public void OneRevision_FilesReadOverItsSummary_TheOwnersNumbers()
    {
        // local_c's latest: 486,506 entries = 415,256 files + 71,249 folders
        // (+ the root); restic's summary said 415,256 files.
        var estimate = Estimate(("C:\\", 415_256));
        Assert.Equal(0, estimate.Percent(0, 0, complete: false));
        Assert.Equal(50, estimate.Percent(0, 207_628, complete: false));
        Assert.Equal(99, estimate.Percent(0, 415_256, complete: false)); // every file listed, the tree not built yet
        Assert.Equal(100, estimate.Percent(0, 415_256, complete: true));

        // Counting folders too would have overshot: 486,506 of 415,256.
        var entries = Estimate(("C:\\", 415_256));
        Assert.Equal(99, entries.Percent(0, 486_506, complete: false));
    }

    [Fact]
    public void SeveralRevisions_WeightedByFileCount_OneBarOverTheWholeMount()
    {
        // A big revision, then a small one: the small one is the last tenth.
        var estimate = Estimate(("C:\\", 900_000), ("C:\\", 100_000));
        Assert.Equal(45, estimate.Percent(0, 450_000, complete: false));
        Assert.Equal(90, estimate.Percent(0, 900_000, complete: true));
        Assert.Equal(90, estimate.Percent(1, 0, complete: false));
        Assert.Equal(95, estimate.Percent(1, 50_000, complete: false));
        Assert.Equal(100, estimate.Percent(1, 100_000, complete: true));

        // Equal revisions: thirds.
        var thirds = Estimate(("a", 10), ("a", 10), ("a", 10));
        Assert.Equal(33, thirds.Percent(0, 10, complete: true));
        Assert.Equal(50, thirds.Percent(1, 5, complete: false));
        Assert.Equal(66, thirds.Percent(1, 10, complete: true));
    }

    [Fact]
    public void ARevisionWithoutASummary_BorrowsAnotherRevisionOfTheSameSource()
    {
        // Revision 2 has no summary (an older snapshot): its neighbour's 1,000.
        var estimate = Estimate(("a", 1_000), ("a", 0));
        Assert.Equal(50, estimate.Percent(0, 1_000, complete: true));
        Assert.Equal(75, estimate.Percent(1, 500, complete: false));
        Assert.Equal(100, estimate.Percent(1, 1_100, complete: true));

        // Never from another source: its size says nothing about this one.
        var other = Estimate(("a", 1_000), ("b", 0));
        Assert.Equal(99, other.Percent(0, 1_000, complete: true));
        Assert.Null(other.Percent(1, 400, complete: false));   // the bar keeps moving for b
        Assert.Equal(100, other.Percent(1, 900, complete: true));
    }

    [Fact]
    public void NoSummaryAtAll_NoPercentage()
    {
        var estimate = Estimate(("a", 0), ("b", 0));
        Assert.Null(estimate.Percent(0, 0, complete: false));
        Assert.Null(estimate.Percent(0, 5_000, complete: true));
        Assert.Null(estimate.Percent(1, 5_000, complete: true));
    }

    [Fact]
    public void NeverBackwards_AndAt99UntilTheLastTreeIsRead()
    {
        var estimate = Estimate(("a", 1_000));
        Assert.Equal(60, estimate.Percent(0, 600, complete: false));
        // The listing's count drops (a stopped listing's files leave the sum).
        Assert.Equal(60, estimate.Percent(0, 400, complete: false));
        // More files than the summary said: still 99% until read in full.
        Assert.Equal(99, estimate.Percent(0, 1_500, complete: false));
        Assert.Equal(100, estimate.Percent(0, 1_500, complete: true));

        // A run of readings across revisions only ever rises.
        var run = Estimate(("a", 300), ("a", 700), ("b", 0), ("b", 200));
        var seen = new List<int?>
        {
            run.Percent(0, 100, false), run.Percent(0, 50, false), run.Percent(0, 300, true),
            run.Percent(1, 0, false), run.Percent(1, 900, false), run.Percent(1, 700, true),
            run.Percent(2, 10, false), run.Percent(2, 150, true),
            run.Percent(3, 10, false), run.Percent(3, 200, false), run.Percent(3, 200, true),
        };
        var shown = seen.Where(p => p is not null).Select(p => p!.Value).ToList();
        Assert.Equal(shown.OrderBy(p => p), shown);
        Assert.All(shown.Take(shown.Count - 1), p => Assert.True(p <= 99));
        Assert.Equal(100, shown.Last());
        // The "b" revision without a summary borrowed the later one's 200:
        // 1,200 of 1,400 once it has been read.
        Assert.Equal(85, seen[7]);
        Assert.Equal(99, seen[9]);
    }
}
