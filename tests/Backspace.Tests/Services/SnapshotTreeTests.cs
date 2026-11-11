using System;
using System.Diagnostics;
using System.Linq;
using Backspace.Services;
using Backspace.Services.WebDav;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U158: a mounted snapshot's folders come from one in-memory tree.</summary>
public sealed class SnapshotTreeTests
{
    private static readonly DateTime When = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Builds_folders_files_and_lookups_case_insensitively_in_enumeration_order()
    {
        var tree = SnapshotTree.Build(new[]
        {
            new RevisionTreeNode("", true, 0, When),
            new RevisionTreeNode("Photos/2026/b.jpg", false, 20, When),   // a file before its folders
            new RevisionTreeNode("Photos", true, 0, When.AddDays(-1)),
            new RevisionTreeNode("Photos/2026", true, 0, When),
            new RevisionTreeNode("Photos/2026/A.jpg", false, 10, When),
            new RevisionTreeNode("readme.txt", false, 5, When),
            new RevisionTreeNode("Empty", true, 0, When),
            new RevisionTreeNode("Photos", true, 0, When.AddDays(-1)),     // a folder twice (two listings)
        });
        Assert.Equal(3, tree.FileCount);
        Assert.Equal(3, tree.DirectoryCount);
        Assert.Equal(35, tree.TotalBytes);
        Assert.Equal(new[] { "Empty", "Photos", "readme.txt" }, tree.Root.Children.Select(c => c.Name));
        Assert.Equal(new[] { "A.jpg", "b.jpg" }, tree.Find("photos/2026")!.Children.Select(c => c.Name));
        Assert.Equal("Photos/2026/A.jpg", tree.Find("PHOTOS\\2026\\a.JPG")!.Path);
        Assert.Equal(When.AddDays(-1), tree.Find("Photos")!.ModifiedUtc);
        Assert.Empty(tree.Find("Empty")!.Children);
        Assert.Null(tree.Find("Photos/2025"));
        Assert.Null(tree.Find("readme.txt/inside"));
        Assert.Same(tree.Root, tree.Find(""));
        var photos = tree.Find("Photos/2026")!;
        Assert.Equal(1, photos.IndexAfter("a.jpg"));
        Assert.Equal(2, photos.IndexAfter("B.JPG"));
        Assert.Equal(0, photos.IndexAfter("0"));
    }

    [Fact]
    public void A_600000_entry_snapshot_builds_in_seconds_and_answers_lookups_in_microseconds()
    {
        var nodes = Enumerable.Range(0, 30_000).SelectMany(d => Enumerable.Range(0, 20)
            .Select(f => new RevisionTreeNode($"top {d % 20}/folder {d}/file {f}.txt", false, f, When)))
            .ToList();
        var clock = Stopwatch.StartNew();
        var tree = SnapshotTree.Build(nodes);
        var built = clock.Elapsed;
        Assert.Equal(600_000, tree.FileCount);
        Assert.Equal(30_020, tree.DirectoryCount);
        clock.Restart();
        for (var i = 0; i < 100_000; i++)
            Assert.NotNull(tree.Find($"top {i % 20}/folder {i % 30_000 - i % 30_000 % 20 + i % 20}/file {i % 20}.txt"));
        var lookups = clock.Elapsed;
        Assert.True(built < TimeSpan.FromSeconds(10), $"Building took {built}.");
        Assert.True(lookups < TimeSpan.FromSeconds(2), $"100,000 look-ups took {lookups}.");
    }

    [Fact]
    public void Folders_are_shared_out_in_turn_and_long_command_lines_are_split()
    {
        var folders = Enumerable.Range(0, 10).Select(i => "/C/folder " + i).ToArray();
        var groups = ResticEngine.ShareOut(folders, 4);
        Assert.Equal(4, groups.Count);
        Assert.Equal(new[] { "/C/folder 0", "/C/folder 4", "/C/folder 8" }, groups[0]);
        Assert.Equal(folders.OrderBy(f => f), groups.SelectMany(g => g).OrderBy(f => f));
        Assert.Equal(2, ResticEngine.ShareOut(folders.Take(2).ToArray(), 8).Count);
        Assert.Empty(ResticEngine.ShareOut(Array.Empty<string>(), 8));
        var longNames = Enumerable.Range(0, 400).Select(i => "/C/" + new string('x', 200) + i).ToArray();
        var split = ResticEngine.ShareOut(longNames, 2);
        Assert.True(split.Count > 2);
        Assert.All(split, g => Assert.True(g.Sum(f => f.Length + 3) < 20_000));
        Assert.Equal(400, split.Sum(g => g.Count));
    }
}
