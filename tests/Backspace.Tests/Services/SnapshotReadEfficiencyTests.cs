using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class SnapshotReadEfficiencyTests
{
    private static readonly string Snapshot = new('a', 64);

    // Which snapshot ids are known (read without listing again) is checked
    // against real restic in DirectReadAndRestoreTests (U175 step 3).

    [Fact]
    public void BulkTreeKeepsNestedUnicodeAndLiteralBracketNames_ExcludesSiblingTrees()
    {
        var listing = Tree();
        listing.Accept(Node("/C/source", "dir"));
        listing.Accept(Node("/C/source/nested", "dir"));
        listing.Accept(Node("/C/source/nested/café日本語[1].txt", "file", 42));
        listing.Accept(Node("/C/source/empty.bin", "file", 0));
        listing.Accept(Node("/C/source-old/secret", "file", 12));
        listing.Accept(Node("/C/Source/secret", "file", 12));
        Assert.True(listing.IsComplete);
        Assert.Equal(new[] { "", "nested", "nested/café日本語[1].txt", "empty.bin" }, listing.Nodes.Select(n => n.Path));
        Assert.Equal(42, listing.Nodes[2].SizeBytes);
        Assert.Equal(0, listing.Nodes[3].SizeBytes);
    }

    [Theory]
    [InlineData("{broken json")]
    [InlineData("{\"struct_type\":\"node\",\"type\":\"file\"}")]
    [InlineData("{\"struct_type\":\"node\",\"path\":\"/C/source/file\",\"type\":\"file\",\"size\":-1}")]
    [InlineData("{\"struct_type\":\"node\",\"path\":\"/C/source/../outside\",\"type\":\"file\"}")]
    [InlineData("{\"struct_type\":\"snapshot\",\"id\":\"another\"}")]
    public void InvalidOutputCannotPublishAPartialTree(string invalid)
    {
        var listing = Tree();
        listing.Accept(Node("/C/source/valid.txt", "file", 10));
        listing.Accept(invalid);
        Assert.False(listing.IsComplete);
    }

    [Fact]
    public void MissingOrWrongSnapshotHeaderCannotPublishATree()
    {
        var missing = new ResticEngine.SnapshotTreeListing("/C/source", Snapshot);
        missing.Accept(Node("/C/source/valid.txt", "file"));
        Assert.False(missing.IsComplete);
        var wrong = new ResticEngine.SnapshotTreeListing("/C/source", Snapshot);
        wrong.Accept("{\"struct_type\":\"snapshot\",\"id\":\"wrong\"}");
        Assert.False(wrong.IsComplete);
        Assert.True(Tree().IsComplete); // An actual empty snapshot is valid.
    }

    private static string Node(string path, string type, long size = 0) => JsonSerializer.Serialize(new { struct_type = "node", path, type, size });

    /// <summary>U152: a link in a snapshot is never offered for a test restore,
    /// the mounted drive or a single-file restore: creating one that leads to
    /// another computer makes Windows sign in there as whoever restores, and
    /// restic's listing doesn't say where a link leads. The line below is the
    /// shape restic 0.19.1 prints for a link (a junction, backed up for real).</summary>
    [Fact]
    public void Links_are_left_out_of_the_listing()
    {
        var listing = Tree();
        listing.Accept(Node("/C/source/report.txt", "file", 10));
        listing.Accept("{\"name\":\"jn\",\"type\":\"symlink\",\"path\":\"/C/source/jn\",\"uid\":0,\"gid\":0,\"mode\":134218166,\"permissions\":\"Lrw-rw-rw-\",\"mtime\":\"2026-11-06T02:27:04.0581786-10:00\",\"atime\":\"2026-11-06T02:27:04.0581786-10:00\",\"ctime\":\"2026-11-06T02:27:04.0581786-10:00\",\"message_type\":\"node\",\"struct_type\":\"node\"}");
        Assert.True(listing.IsComplete);
        Assert.Equal(new[] { "report.txt" }, listing.Nodes.Select(n => n.Path).Where(p => p != ""));
    }

    [Theory]
    [InlineData("C:\\")] [InlineData("C:/")]
    public void WholeDriveListingKeepsRootFilesAndExcludesOtherDrives(string source)
    {
        var prefix = ResticEngine.SourcePathToSnapshotPrefix(source);
        Assert.Equal("C", prefix);
        var listing = new ResticEngine.SnapshotTreeListing("/" + prefix, Snapshot);
        listing.Accept(JsonSerializer.Serialize(new { struct_type = "snapshot", id = Snapshot }));
        listing.Accept(Node("/C", "dir"));
        listing.Accept(Node("/C/root.txt", "file", 10));
        listing.Accept(Node("/C/nested/file.txt", "file", 20));
        listing.Accept(Node("/C-old/wrong.txt", "file", 30));
        listing.Accept(Node("/D/wrong.txt", "file", 40));
        Assert.True(listing.IsComplete);
        Assert.Equal(new[] { "", "root.txt", "nested/file.txt" }, listing.Nodes.Select(n => n.Path));
    }
    /// <summary>U166: the mount's progress counts files, not folders, so it
    /// compares with the snapshot summary's file count (the owner's snapshot:
    /// 486,506 entries, 415,256 files, and its summary said 415,256 files).</summary>
    [Fact]
    public void Progress_counts_files_not_folders()
    {
        var heard = new List<int>();
        var listing = new ResticEngine.SnapshotTreeListing("/C/source", Snapshot, new Counted(heard.Add));
        listing.Accept(JsonSerializer.Serialize(new { struct_type = "snapshot", id = Snapshot }));
        listing.Accept(Node("/C/source", "dir"));
        for (var folder = 0; folder < 1000; folder++)
        {
            listing.Accept(Node($"/C/source/f{folder}", "dir"));
            for (var file = 0; file < 9; file++) listing.Accept(Node($"/C/source/f{folder}/{file}.txt", "file", 1));
        }
        Assert.True(listing.IsComplete);
        Assert.Equal(10_001, listing.Nodes.Count);
        Assert.Equal(9_000, listing.Files);
        // Heard every 5,000 entries: the files among them, never the folders.
        Assert.Equal(new[] { 4_499, 8_999 }, heard);
    }

    private sealed class Counted(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private static ResticEngine.SnapshotTreeListing Tree()
    {
        var listing = new ResticEngine.SnapshotTreeListing("/C/source", Snapshot);
        listing.Accept(JsonSerializer.Serialize(new { struct_type = "snapshot", id = Snapshot }));
        return listing;
    }
}
