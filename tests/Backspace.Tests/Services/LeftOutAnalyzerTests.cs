using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U177 (10 November 2026): "What these rules leave out", on real folders.
/// One row per topmost item left out, the rule responsible as restic decides
/// (the last that matches, exceptions included), rules that match nothing,
/// totals, and the comparison with the saved rules.
/// </summary>
public sealed class LeftOutAnalyzerTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "backspace-leftout-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string P(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private LeftOutResult Run(string rules, string? saved = null, bool skipCloud = true)
        => LeftOutAnalyzer.Analyze(new LeftOutRequest(new[] { _root }, rules, saved, skipCloud), new LeftOutCounters(), CancellationToken.None);

    [Fact]
    public void A_folder_left_out_is_one_row_with_its_count_and_size_never_its_contents()
    {
        Write("cache/a.bin", 1_000);
        Write("cache/deep/b.bin", 2_000);
        Write("cache/deep/c.tmp", 500);
        Write("docs/report.txt", 10);
        Write("docs/draft.tmp", 300);
        Write("keep.txt", 5);

        var r = Run("cache/\n*.tmp");

        Assert.Equal(2, r.Items.Count);
        var cache = r.Items[0];
        Assert.Equal(P("cache"), cache.Path);
        Assert.True(cache.IsFolder);
        Assert.Equal(3, cache.Files);
        Assert.Equal(3_500, cache.Bytes);
        Assert.Equal("cache/", cache.Rule);
        // The *.tmp inside the cache folder is not a row of its own: restic never looks.
        Assert.DoesNotContain(r.Items, i => i.Path.Contains("deep", StringComparison.Ordinal));
        var draft = r.Items[1];
        Assert.Equal(P("docs/draft.tmp"), draft.Path);
        Assert.False(draft.IsFolder);
        Assert.Equal("*.tmp", draft.Rule);
        // Sorted by size, and the totals add up.
        Assert.Equal(4, r.TotalFiles);
        Assert.Equal(3_800, r.TotalBytes);
        Assert.Empty(r.MatchNothing);
        Assert.Empty(r.Unreadable);
    }

    [Fact]
    public void The_rule_responsible_is_the_last_that_matches_and_an_exception_keeps()
    {
        Write("logs/app.log", 100);
        Write("logs/keep/important.log", 200);
        Write("logs/other/x.log", 300);

        // logs/* leaves out each child; the exception keeps logs/keep; *.log,
        // written after, leaves out the log inside the kept folder again.
        var r = Run("logs/*\n!logs/keep\n*.log");

        var rows = r.Items.ToDictionary(i => i.Path, i => i.Rule);
        Assert.Equal("*.log", rows[P("logs/app.log")]); // both match: the last decides
        Assert.Equal("logs/*", rows[P("logs/other")]);  // a folder: decided before *.log is tried inside
        Assert.Equal("*.log", rows[P("logs/keep/important.log")]);
        Assert.DoesNotContain(P("logs/keep"), rows.Keys);
        Assert.Equal(3, r.Items.Count);
        // Every rule matched something; the exception kept a folder.
        Assert.Empty(r.MatchNothing);

        // Without the later *.log the exception decides: nothing in keep is left out.
        var kept = Run("logs/*\n!logs/keep");
        Assert.DoesNotContain(kept.Items, i => i.Path.StartsWith(P("logs/keep"), StringComparison.Ordinal));
        Assert.Equal(new[] { P("logs/other"), P("logs/app.log") }, kept.Items.Select(i => i.Path));
        // By rule: one rule, two items, their totals.
        var total = Assert.Single(kept.ByRule);
        Assert.Equal(("logs/*", 2, 2L, 400L), (total.Rule, total.Items, total.Files, total.Bytes));
    }

    [Fact]
    public void Rules_that_match_nothing_are_listed_with_their_lines()
    {
        Write("a.txt", 1);
        var text = "# my rules\n*.iso\n\n/top.txt\n!nothing-here\na.txt";
        var r = Run(text);
        var unused = r.MatchNothing.ToDictionary(u => u.Rule);
        Assert.Equal(new[] { "*.iso", "/top.txt", "!nothing-here" }, r.MatchNothing.Select(u => u.Rule));
        Assert.Equal(1, unused["*.iso"].Line);
        Assert.Equal(3, unused["/top.txt"].Line);
        Assert.Equal(OperatingSystem.IsWindows(), unused["/top.txt"].Rooted);
        Assert.False(unused["*.iso"].Rooted);
        Assert.Equal(5, Assert.Single(r.Items).RuleLine);
        // A rule matching only inside a folder already left out matches nothing:
        // restic never looks in there.
        Write("cache/x.iso", 1);
        Assert.Contains(Run("cache/\n*.iso").MatchNothing, u => u.Rule == "*.iso");
    }

    [Fact]
    public void Recommended_rules_matching_nothing_are_marked_as_such()
    {
        Write("a.txt", 1);
        var block = RecommendedFilters.RenderBlock(false, true, Array.Empty<RecommendedFilters.CloudSyncFolder>(), new[] { _root });
        var r = Run(RecommendedFilters.MergeIntoFilters("mine.iso", block));
        Assert.Contains(r.MatchNothing, u => u.Rule == "mine.iso" && !u.Recommended);
        Assert.Contains(r.MatchNothing, u => u.Rule == "*.tmp" && u.Recommended);
    }

    [Fact]
    public void The_backed_up_folder_itself_can_be_left_out_whole()
    {
        Write("a.txt", 10);
        Write("sub/b.txt", 20);
        var name = Path.GetFileName(_root);
        var r = Run(name);
        var whole = Assert.Single(r.Items);
        Assert.Equal(TreeWalk.Root(_root), whole.Path);
        Assert.Equal(2, whole.Files);
        Assert.Equal(30, whole.Bytes);
    }

    [Fact]
    public void Compared_with_the_saved_rules_new_rows_are_marked_and_back_in_is_listed()
    {
        Write("build/out.bin", 4_000);
        Write("build/obj/x.bin", 1_000);
        Write("videos/a.mp4", 9_000);
        Write("notes.txt", 1);

        // Saved: videos and build left out. Edited: build kept, notes.txt left out.
        var r = Run("videos/\nnotes.txt", saved: "videos/\nbuild/");
        Assert.True(r.Compared);
        Assert.False(r.Items.Single(i => i.Path == P("videos")).IsNew);
        Assert.True(r.Items.Single(i => i.Path == P("notes.txt")).IsNew);
        var back = Assert.Single(r.BackIn);
        Assert.Equal(P("build"), back.Path);
        Assert.Equal((2L, 5_000L), (back.Files, back.Bytes));
        Assert.Equal("build/", back.SavedRule);

        // The same text as saved: nothing compared, nothing marked.
        var same = Run("videos/", saved: "videos/\n");
        Assert.False(same.Compared);
        Assert.Empty(same.BackIn);
        Assert.DoesNotContain(same.Items, i => i.IsNew);
    }

    [Fact]
    public void A_cancelled_run_stops()
    {
        for (var i = 0; i < 50; i++) Write($"d{i}/f.txt", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LeftOutAnalyzer.Analyze(new LeftOutRequest(new[] { _root }, "*.txt", null, true), new LeftOutCounters(), cts.Token));
    }

    [Fact]
    public void A_missing_folder_is_listed_as_unreadable()
    {
        var r = LeftOutAnalyzer.Analyze(new LeftOutRequest(new[] { P("not-there") }, "*.tmp", null, true), new LeftOutCounters(), CancellationToken.None);
        Assert.Empty(r.Items);
        Assert.Equal(new[] { P("not-there") }, r.Unreadable);
    }

    [Fact]
    public void A_link_is_never_followed()
    {
        if (!OperatingSystem.IsWindows()) return;
        Write("real/big.bin", 50_000);
        Directory.CreateDirectory(P("holder"));
        try { Directory.CreateSymbolicLink(P("holder/link"), P("real")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // needs Developer Mode
        // Left out: a row with nothing counted, the target untouched.
        var r = Run("link");
        var link = Assert.Single(r.Items);
        Assert.True(link.IsLink);
        Assert.Equal((0L, 0L), (link.Files, link.Bytes));
        // Kept: the link is not gone into, so its target's file is not left out twice.
        var big = Run("big.bin");
        Assert.Equal(P("real/big.bin"), Assert.Single(big.Items).Path);
    }

    [Fact]
    public void Keep_this_writes_an_exact_exception()
    {
        Assert.Equal("!C:/Users/me/[[]draft]/a$$b", BackupEditorViewModel.KeepRule(@"C:\Users\me\[draft]\a$b"));
        Assert.Equal("!C:/Users/me/x", BackupEditorViewModel.KeepRule(@"C:\Users\me\x\"));
        // And it works: the exception, last, keeps the folder a rule left out.
        Write("cache/a.bin", 1);
        var rule = BackupEditorViewModel.KeepRule(P("cache"));
        Assert.Single(Run("cache/").Items);
        Assert.Empty(Run("cache/\n" + rule).Items);
    }
}
