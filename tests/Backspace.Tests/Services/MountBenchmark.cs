using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.Services.WinFsp;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Manual timing of a mounted snapshot of a large backup (U158). Set
/// BACKSPACE_MOUNT_BENCH to a folder holding "src" (the files to back up);
/// the first run backs it up into "repo" beside it, later runs reuse that
/// repository. Writes its timings to "results.txt" in that folder. Skipped
/// otherwise: the fixture takes minutes to build.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountBenchmark(ITestOutputHelper output)
{
    [Fact]
    public async Task Time_a_mounted_large_snapshot()
    {
        var root = Environment.GetEnvironmentVariable("BACKSPACE_MOUNT_BENCH");
        if (string.IsNullOrEmpty(root) || !WinFspRuntimeDetector.IsInstalled()) Assert.Skip("Set BACKSPACE_MOUNT_BENCH to run the mount benchmark.");
        await using var fixture = await RealEngineFixture.TryCreateAsync(output);
        if (fixture is null) Assert.Skip("Engines missing.");
        var source = Path.Combine(root!, "src", "C");
        var repo = Path.Combine(root!, "repo");
        var destination = new Destination { Id = "u158bench-dest", Name = "Bench", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = repo, Encrypted = true, StoragePasswordRef = ServiceLocator.Secrets.PutNew("u158-bench", "u158-bench-password") };
        var backup = new Backup { Id = "u158bench", Name = "Bench", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            Threads = 8, Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        var report = new StringBuilder();
        void Note(string line) { output.WriteLine(line); report.AppendLine(line); }
        var watch = Stopwatch.StartNew();
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, destination, "default", CancellationToken.None);
        if (revisions.Count == 0)
        {
            var run = await fixture.Engine.BackupPruneCheckAsync(backup, source, destination, "default", CancellationToken.None);
            Note($"backup: {run.Status} in {watch.Elapsed.TotalSeconds:F1} s {run.Summary} {string.Join(" | ", run.Errors)}");
            Assert.NotEqual(BackupRunStatus.Failed, run.Status);
            revisions = await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, destination, "default", CancellationToken.None);
        }
        Note($"revisions listed: {revisions.Count} ({watch.ElapsedMilliseconds} ms incl. any backup)");

        string Measure(string label, Func<object?> action)
        {
            var sw = Stopwatch.StartNew();
            var result = action();
            Note($"{label}: {sw.ElapsedMilliseconds} ms {(result is int n ? $"({n} entries)" : "")}");
            return "";
        }

        var cache = Path.Combine(root!, "mount-cache-" + Guid.NewGuid().ToString("N")[..8]);
        watch.Restart();
        var provider = new BackupMountTreeProvider(fixture.Engine, backup, destination, "default", cache);
        await using var mount = new WinFspMountCoordinator(provider, openExplorer: false);
        var mounted = await mount.MountAsync(CancellationToken.None);
        Assert.True(mounted.Succeeded, mounted.Message);
        Note($"mount ready: {watch.ElapsedMilliseconds} ms at {mounted.DriveLetter}; managed memory after a full collection: {GC.GetTotalMemory(true) / (1024 * 1024)} MB");
        var latest = mounted.DriveLetter + @"\Latest revision\C";
        Measure("first listing of the source folder", () => Directory.GetFileSystemEntries(latest).Length);
        Measure("listing a third-level folder", () => Directory.GetFileSystemEntries(Path.Combine(latest, "Users", "folder 03")).Length);
        Measure("listing a deep folder", () => Directory.GetFileSystemEntries(Path.Combine(latest, "Photos", "folder 07", "sub 04 batch0", "leaf 6")).Length);
        Measure("listing it again", () => Directory.GetFileSystemEntries(Path.Combine(latest, "Photos", "folder 07", "sub 04 batch0", "leaf 6")).Length);
        Measure("listing a 20,000-file folder", () => Directory.GetFileSystemEntries(Path.Combine(latest, "Windows", "WinSxS")).Length);
        Measure("listing it again", () => Directory.GetFileSystemEntries(Path.Combine(latest, "Windows", "WinSxS")).Length);
        Measure("recursive walk of one top folder (sizes)", () =>
            new DirectoryInfo(Path.Combine(latest, "Music")).EnumerateFiles("*", SearchOption.AllDirectories).Count());
        var folder = Path.Combine(latest, "Documents", "folder 02", "sub 01 batch0", "leaf 3");
        Measure("opening and reading a file", () => File.ReadAllBytes(Path.Combine(folder, "file 05 é.txt")).Length);
        Measure("reading it again", () => File.ReadAllBytes(Path.Combine(folder, "file 05 é.txt")).Length);
        Measure("reading a sibling file", () => File.ReadAllBytes(Path.Combine(folder, "file 06 é.txt")).Length);
        Measure("reading 5 more siblings", () => Enumerable.Range(7, 5).Sum(i => File.ReadAllBytes(Path.Combine(folder, $"file {i:D2} é.txt")).Length));
        var expected = File.ReadAllBytes(Path.Combine(source, "Documents", "folder 02", "sub 01 batch0", "leaf 3", "file 05 é.txt"));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(folder, "file 05 é.txt")));
        var older = mounted.DriveLetter + @"\All revisions";
        Measure("listing All revisions", () => Directory.GetFileSystemEntries(older).Length);
        await mount.DisposeAsync();
        try { Directory.Delete(cache, recursive: true); } catch { }
        File.AppendAllText(Path.Combine(root!, "results.txt"), $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.GetEnvironmentVariable("BACKSPACE_MOUNT_BENCH_LABEL")}\n" + report);
    }
}
