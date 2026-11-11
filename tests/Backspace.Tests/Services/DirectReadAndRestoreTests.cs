using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U175 step 3: snapshot lists, folder listings, the mounted drive's reads and
/// every restore run restic directly. Real restic, a real repository. What Backrest's path guaranteed still holds: newest first,
/// source-relative paths, only the requested files promoted, an original kept
/// through an error or a cancel, and nothing restored is an error.
/// </summary>
public sealed class DirectReadAndRestoreTests(ITestOutputHelper output)
{
    /// <summary>Four files, one with brackets, one with an accent in a
    /// folder beside an empty folder, and the name the pattern
    /// <c>selected[draft].txt</c> matches unless its bracket is escaped.</summary>
    private static void Files(string source, int bigMiB = 0)
    {
        File.WriteAllText(Path.Combine(source, "kept.txt"), "kept");
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        File.WriteAllText(Path.Combine(source, "nested", "café [1].txt"), "unicode and brackets");
        File.WriteAllText(Path.Combine(source, "selected[draft].txt"), "selected snapshot");
        File.WriteAllText(Path.Combine(source, "selectedd.txt"), "overmatch in the snapshot");
        if (bigMiB > 0) File.WriteAllBytes(Path.Combine(source, "big.bin"), RandomNumberGenerator.GetBytes(bigMiB << 20));
    }

    private static readonly string[] AllFiles = ["kept.txt", "nested/café [1].txt", "selected[draft].txt", "selectedd.txt"];

    private static Task<IReadOnlyList<RevisionSummary>> List(ResticRig rig) =>
        rig.Engine.ListSnapshotsAsync(rig.Backup, rig.Source, rig.Destination, "default", TestContext.Current.CancellationToken);

    private static Task<ResticBackrestEngine.ResolvedSnapshot> Resolve(ResticRig rig, string? id) =>
        rig.Engine.ResolveSnapshotIdAsync(rig.Backup, rig.Source, rig.Destination, "default", 1, TestContext.Current.CancellationToken, id);

    private static Task<RestoreOpResult> Restore(ResticRig rig, string target, string[] includes, string[]? leaves = null,
        string? snapshot = null, CancellationToken ct = default, Action<RestoreProgressUpdate>? progress = null) =>
        rig.Engine.RestoreFilesAsync(rig.Backup, rig.Source, rig.Destination, "default", 1, includes, target, progress, ct,
            expectedLeaves: leaves, expectedSnapshotId: snapshot);

    private static bool IsList((ResticCommand Command, ResticResult Result) c) =>
        c.Command.Arguments.SequenceEqual(["snapshots", "--json", "--no-lock"]);

    [Fact]
    public async Task Snapshots_files_folders_and_a_files_bytes_are_read_by_restic_alone()
    {
        await using var rig = new ResticRig("reads", source => Files(source));
        var first = await rig.BackUpAsync();
        File.WriteAllText(Path.Combine(rig.Source, "second.txt"), "added later");
        var second = await rig.BackUpAsync();
        rig.Commands.Clear();

        // Newest first, numbered from the oldest, with restic's file counts.
        var snapshots = await List(rig);
        Assert.Equal(new[] { second, first }, snapshots.Select(s => s.SnapshotId));
        Assert.Equal(new[] { 2, 1 }, snapshots.Select(s => s.Number));
        Assert.Equal(new[] { 5, 4 }, snapshots.Select(s => s.FileCount));
        // Backrest's list of a plan's snapshots, without a lock.
        var listing = Assert.Single(rig.Commands);
        Assert.True(IsList(listing));
        Assert.Equal(new[] { "--tag", $"plan:{rig.PlanId},created-by:backspace" }, listing.Command.Trailing!);

        // The older snapshot's files: relative to the source, sorted, no
        // folders, not the later file.
        var files = await rig.Engine.ListSnapshotFilesAsync(rig.Backup, rig.Source, rig.Destination, "default", 1, default, first);
        Assert.Equal(AllFiles, files.Select(f => f.Path));
        Assert.Equal(4, files.Single(f => f.Path == "kept.txt").SizeBytes);
        Assert.Contains(await rig.Engine.ListSnapshotFilesAsync(rig.Backup, rig.Source, rig.Destination, "default", 2, default, second),
            f => f.Path == "second.txt");

        // One folder at a time (the mounted drive's fallback): its children only.
        Task<IReadOnlyList<RevisionDirEntry>?> Folder(string path) =>
            rig.Engine.ListSnapshotDirectoryAsync(rig.Backup, rig.Source, rig.Destination, "default", 1, path, default, first);
        // Sorted as Explorer-like listings were, ignoring case ("D" before "[").
        var top = await Folder("");
        Assert.Equal(new[] { ("kept.txt", false), ("nested", true), ("selectedd.txt", false), ("selected[draft].txt", false) },
            top!.Select(e => (e.Name, e.IsDirectory)));
        Assert.Equal(new[] { ("café [1].txt", false), ("empty", true) }, (await Folder("nested"))!.Select(e => (e.Name, e.IsDirectory)));
        Assert.Empty((await Folder("nested/empty"))!);
        // Not there, or not a folder: "no such folder", never a guess.
        Assert.Null(await Folder("missing"));
        Assert.Null(await Folder("kept.txt"));

        // One file's bytes, as the mounted drive copies it out.
        using var bytes = new MemoryStream();
        Assert.True(await rig.Engine.DumpSnapshotFileAsync(rig.Backup, rig.Source, rig.Destination, "default", 1, "nested/café [1].txt",
            bytes, default, first));
        Assert.Equal("unicode and brackets", Encoding.UTF8.GetString(bytes.ToArray()));
        // Everything after the first list read the snapshot it knew.
        Assert.Single(rig.Commands, IsList);
    }

    [Fact]
    public async Task A_snapshot_listed_once_is_read_without_listing_again_but_a_position_or_unknown_id_lists()
    {
        await using var rig = new ResticRig("known", source => Files(source));
        var id = await rig.BackUpAsync();
        rig.Commands.Clear();
        await List(rig);
        Assert.Equal(id, (await Resolve(rig, id)).SnapshotId);
        Assert.Equal(id, (await Resolve(rig, id)).SnapshotId);
        Assert.Single(rig.Commands, IsList);
        Assert.Equal(id, (await Resolve(rig, null)).SnapshotId);
        Assert.Equal(2, rig.Commands.Count(IsList));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(rig, new string('b', 64)));
        Assert.Contains("no other version was substituted", error.Message);
        Assert.Equal(3, rig.Commands.Count(IsList));
    }

    [Theory]
    [InlineData("address")]
    [InlineData("plan")]
    [InlineData("source")]
    public async Task Another_address_plan_or_source_cannot_reuse_a_known_snapshot(string change)
    {
        await using var rig = new ResticRig("known-" + change, source => Files(source));
        var id = await rig.BackUpAsync();
        await List(rig);
        rig.Commands.Clear();
        var source = rig.Source;
        switch (change)
        {
            case "address": rig.Destination.PathOrSubpath = rig.Workspace.Sub("another-repository"); break;
            case "plan": rig.Backup.Id = "another-backup"; break;
            case "source": source = rig.Workspace.Sub("another-source"); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Engine.ResolveSnapshotIdAsync(rig.Backup, source, rig.Destination, "default", 1, default, id));
        Assert.Single(rig.Commands, IsList);
    }

    [Fact]
    public async Task A_new_list_forgets_a_removed_snapshot()
    {
        await using var rig = new ResticRig("known-removed", source => Files(source));
        var id = await rig.BackUpAsync();
        Assert.Single(await List(rig));
        await rig.ResticAsync("forget", id);
        Assert.Empty(await List(rig));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolve(rig, id));
    }

    /// <summary>Nothing backed up there yet is no snapshot, and looking creates
    /// no repository; a local destination whose folder is missing (a drive
    /// not connected) is an error, never an empty history.</summary>
    [Fact]
    public async Task No_repository_yet_is_no_snapshot_but_a_missing_folder_is_an_error()
    {
        await using var rig = new ResticRig("no-repository", source => Files(source));
        Assert.Empty(await List(rig));
        Assert.Empty(Directory.EnumerateFileSystemEntries(rig.Destination.PathOrSubpath));
        rig.Destination.PathOrSubpath = Path.Combine(rig.Workspace.Root, "unplugged");
        var error = await Assert.ThrowsAsync<IOException>(() => List(rig));
        Assert.Contains("could not list", error.Message);
        Assert.False(Directory.Exists(rig.Destination.PathOrSubpath));
    }

    [Fact]
    public async Task A_restore_promotes_only_the_files_asked_for_and_keeps_everything_else()
    {
        await using var rig = new ResticRig("restore-selected", source => Files(source));
        var id = await rig.BackUpAsync();
        var target = rig.Workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "keep original");
        File.WriteAllText(Path.Combine(target, "selectedd.txt"), "keep sibling");
        string[] picked = ["selected[draft].txt", "nested/café [1].txt"];
        var result = await Restore(rig, target, picked, picked, id);
        Assert.True(result.Status == RestoreTerminalStatus.Success, result.ErrorMessage);
        Assert.Equal(2, result.FilesRestored);
        Assert.Equal(2, result.TotalFiles);
        Assert.Equal("selected snapshot", File.ReadAllText(Path.Combine(target, "selected[draft].txt")));
        Assert.Equal("unicode and brackets", File.ReadAllText(Path.Combine(target, "nested", "café [1].txt")));
        Assert.Equal("keep original", File.ReadAllText(Path.Combine(target, "unrelated.txt")));
        Assert.Equal("keep sibling", File.ReadAllText(Path.Combine(target, "selectedd.txt")));
        Assert.False(File.Exists(Path.Combine(target, "kept.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "nested", "empty")));
        Assert.Empty(Directory.GetDirectories(target, ".backspace-restore-*"));
    }

    [Fact]
    public async Task A_whole_source_restore_brings_back_every_file_and_empty_folder()
    {
        await using var rig = new ResticRig("restore-whole", source => Files(source));
        await rig.BackUpAsync();
        var target = rig.Workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "existing.txt"), "keep original");
        var result = await Restore(rig, target, [""]);
        Assert.True(result.Status == RestoreTerminalStatus.Success, result.ErrorMessage);
        Assert.Equal(4, result.FilesRestored);
        foreach (var file in AllFiles)
            Assert.Equal(File.ReadAllText(Path.Combine(rig.Source, file)), File.ReadAllText(Path.Combine(target, file)));
        Assert.True(Directory.Exists(Path.Combine(target, "nested", "empty")));
        Assert.Equal("keep original", File.ReadAllText(Path.Combine(target, "existing.txt")));
        Assert.Empty(Directory.GetDirectories(target, ".backspace-restore-*"));
    }

    [Fact]
    public async Task A_restore_that_brings_nothing_back_is_an_error_and_part_of_it_a_warning()
    {
        await using var rig = new ResticRig("restore-missing", source => Files(source));
        var id = await rig.BackUpAsync();
        var none = await Restore(rig, rig.Workspace.Sub("none"), ["missing.txt"], ["missing.txt"], id);
        Assert.Equal(RestoreTerminalStatus.Error, none.Status);
        Assert.Equal(0, none.FilesRestored);
        Assert.NotNull(none.ErrorMessage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(rig.Workspace.Sub("none")));
        var part = await Restore(rig, rig.Workspace.Sub("part"), ["kept.txt", "missing.txt"], ["kept.txt", "missing.txt"], id);
        Assert.Equal(RestoreTerminalStatus.Warning, part.Status);
        Assert.Equal((1, 2, 4L), (part.FilesRestored, part.TotalFiles, part.BytesRestored));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(rig.Workspace.Root, "part", "kept.txt")));
    }

    /// <summary>A real restic error (the file's data damaged in this test's
    /// own repository) never turns staged output into recovered data.</summary>
    [Fact]
    public async Task A_damaged_repository_leaves_the_original_in_place()
    {
        await using var rig = new ResticRig("restore-damaged", source => Files(source, bigMiB: 4));
        var id = await rig.BackUpAsync();
        var packs = Directory.GetFiles(Path.Combine(rig.Destination.PathOrSubpath, "data"), "*", SearchOption.AllDirectories)
            .Where(p => new FileInfo(p).Length > 1024 * 1024).ToArray();
        Assert.NotEmpty(packs);
        foreach (var pack in packs) File.WriteAllBytes(pack, [1, 2, 3]);
        var target = rig.Workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "big.bin"), "original survives");
        var result = await Restore(rig, target, ["big.bin"], ["big.bin"], id);
        Assert.Equal(RestoreTerminalStatus.Error, result.Status);
        Assert.Equal(0, result.FilesRestored);
        Assert.Equal("original survives", File.ReadAllText(Path.Combine(target, "big.bin")));
        Assert.Empty(Directory.GetDirectories(target, ".backspace-restore-*"));
    }

    /// <summary>Cancel stops restic before the staging folder goes; the
    /// original stays. The cleanup right after it still runs: a lock the
    /// stopped restore left is a stale one, which the cleanup's unlock
    /// removes.</summary>
    [Fact]
    public async Task Cancelling_a_restore_stops_restic_and_keeps_the_original()
    {
        await using var rig = new ResticRig("restore-cancel", source => Files(source, bigMiB: 8));
        var id = await rig.BackUpAsync();
        rig.Backup.BandwidthLimitKBps = 256; // about 32 s for the file: stopped long before
        var target = rig.Workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "big.bin"), "original survives a cancel");
        using var stop = new CancellationTokenSource();
        long moved = 0;
        var result = await Restore(rig, target, ["big.bin"], ["big.bin"], id, stop.Token, progress =>
        {
            if (progress.BytesRestored > 0 && Interlocked.Exchange(ref moved, progress.BytesRestored) == 0) stop.Cancel();
        }).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        output.WriteLine($"Cancelled after {Interlocked.Read(ref moved):N0} bytes: {result.Status}, {result.ErrorMessage}");
        Assert.Equal(RestoreTerminalStatus.Cancelled, result.Status);
        Assert.True(Interlocked.Read(ref moved) > 0);
        Assert.Equal("original survives a cancel", File.ReadAllText(Path.Combine(target, "big.bin")));
        Assert.Empty(Directory.GetDirectories(target, ".backspace-restore-*"));
        rig.Backup.BandwidthLimitKBps = 0;
        await rig.Engine.PruneExhaustiveAsync(rig.Backup, rig.Source, rig.Destination, "default", TestContext.Current.CancellationToken);
        Assert.True(rig.Ran("prune").Last().Result.Succeeded);
    }
}
