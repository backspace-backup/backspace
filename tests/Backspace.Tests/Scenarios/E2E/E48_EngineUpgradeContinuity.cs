using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>Creates actual old-engine data, then reopens it with the current
/// engine. The old restic is an explicit local test input (restic 0.18.1, the
/// version Backspace bundled with Backrest 1.13.0); the backups it makes carry
/// the labels and command lines every earlier Backspace used (U175 kept them).
/// The test never replaces installed binaries or opens the user's repositories.</summary>
[Collection(ScenarioCollection.Name)]
public sealed class E48_EngineUpgradeContinuity : ScenarioBase
{
    protected override string ScenarioName => "E48-engine-upgrade-continuity";
    [AvaloniaFact] public async Task Run_E48() => await Run();

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var legacyDirectory = Environment.GetEnvironmentVariable("BACKSPACE_PREVIOUS_ENGINE_DIRECTORY");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(legacyDirectory))
            ctx.Skip("This Windows upgrade rehearsal requires BACKSPACE_PREVIOUS_ENGINE_DIRECTORY containing restic-0.18.1.exe.");
        var legacyRestic = Path.Combine(legacyDirectory!, "restic-0.18.1.exe");
        if (!File.Exists(legacyRestic) || !RealEngineFixture.BinariesPresent)
            ctx.Skip("The previous restic and all current bundled engine binaries must be present for the upgrade rehearsal.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var ct = deadline.Token;
        using var workspace = new TempWorkspace("engine-upgrade");
        var secrets = new SecretsStore();
        var source = workspace.Sub("source");
        var destination = new Destination
        {
            Name = "Upgrade rehearsal", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = workspace.Sub("repository"), Encrypted = true,
            StoragePasswordRef = secrets.PutNew("e48", Guid.NewGuid().ToString("N")),
        };
        var backup = new Backup
        {
            Name = "Existing backup", UseVss = false, VssDisabledAcknowledged = true, SourcePaths = { source },
            PruneSchedule = new(), CheckSchedule = new(),
            Targets = { new() { DestinationId = destination.Id, StorageName = "default" } },
        };
        var planId = ResticEngine.MakePlanId(backup, source, destination, "default");
        var oldResticVersion = await ReadVersion(legacyRestic, ct, "version");
        var newResticVersion = await ReadVersion(AppPaths.ResticBinary, ct, "version");
        Assert.Contains("0.18.1", oldResticVersion);
        Assert.NotEqual(oldResticVersion, newResticVersion);
        ctx.Report.RecordNote("Actual engine versions", $"Before: {oldResticVersion}. After: {newResticVersion}.");

        var versions = Enumerable.Range(1, 3).ToDictionary(v => v, ContentsForVersion);
        var snapshotIds = new Dictionary<int, string>();
        var old = new ResticEngine(secrets, legacyRestic);
        for (var version = 1; version <= 2; version++)
        {
            WriteSource(source, versions[version], version);
            var result = await old.BackupPruneCheckAsync(backup, source, destination, "default", ct);
            Assert.Equal(BackupRunStatus.Success, result.Status);
            var snapshots = await old.ListSnapshotsAsync(backup, source, destination, "default", ct);
            Assert.Equal(version, snapshots.Count);
            snapshotIds.Add(version, snapshots.Single(s => s.Number == version).SnapshotId!);
        }
        ctx.Report.RecordNote("Existing backups created before the upgrade",
            "restic 0.18.1 created two encrypted snapshots with different file contents, labelled plan:<id> and created-by:backspace as every earlier Backspace labelled them.");

        // Nothing remembered, as on a computer that only ran the old version:
        // the first backup's parent comes from the plan's snapshot list.
        File.Delete(MaintenanceRecords.OwnFile);
        var engine = new ResticEngine(secrets);
        var inherited = await engine.ListSnapshotsAsync(backup, source, destination, "default", ct);
        Assert.Equal(2, inherited.Count);
        Assert.All(snapshotIds.Values, id => Assert.Contains(inherited, snapshot => snapshot.SnapshotId == id));
        var children = await engine.ListSnapshotDirectoryAsync(backup, source, destination, "default", 1, "", ct, snapshotIds[1]);
        Assert.NotNull(children);
        Assert.Contains(children, entry => entry.Name == "document.txt" && !entry.IsDirectory);
        Assert.Contains(children, entry => entry.Name == "records [old]" && entry.IsDirectory);
        var files = await engine.ListSnapshotFilesAsync(backup, source, destination, "default", 1, ct, snapshotIds[1]);
        Assert.Equal(versions[1].Keys.OrderBy(p => p), files.Select(f => f.Path).OrderBy(p => p));
        await RestoreAndCompare(engine, backup, source, destination, snapshotIds[1], 1,
            workspace.Sub("restore-before-new-backup"), versions[1], ct);
        ctx.Report.RecordNote("Oldest backup is readable immediately",
            "The current engine listed both snapshots and the original three files, including a folder with brackets in its name. A real restore recovered version 1 byte-for-byte while the source still contained version 2.");

        WriteSource(source, versions[3], 3);
        var commands = new System.Collections.Concurrent.ConcurrentQueue<ResticCommand>();
        engine.ResticCommandFinished += (command, _) => commands.Enqueue(command);
        var nextBackup = await engine.BackupPruneCheckAsync(backup, source, destination, "default", ct);
        Assert.Equal(BackupRunStatus.Success, nextBackup.Status);
        // Its parent is the old engine's latest snapshot.
        var arguments = ResticRunner.Arguments(Assert.Single(commands, c => c.Arguments[0] == "backup")).ToList();
        Assert.Equal(snapshotIds[2], arguments[arguments.IndexOf("--parent") + 1]);
        var allSnapshots = await engine.ListSnapshotsAsync(backup, source, destination, "default", ct);
        Assert.Equal(3, allSnapshots.Count);
        Assert.All(snapshotIds.Values, id => Assert.Contains(allSnapshots, snapshot => snapshot.SnapshotId == id));
        snapshotIds.Add(3, allSnapshots.Single(s => s.Number == 3).SnapshotId!);
        Assert.Equal(3, snapshotIds.Values.Distinct().Count());
        foreach (var version in Enumerable.Range(1, 3))
            await RestoreAndCompare(engine, backup, source, destination, snapshotIds[version], version,
                workspace.Sub("restored-version-" + version), versions[version], ct);
        Assert.Equal(snapshotIds[3], MaintenanceRecords.RememberedSnapshot(planId));
        ctx.Report.RecordNote("New backups continue the same history",
            "The current engine added version 3 to the existing encrypted repository, with the old engine's latest snapshot as its parent. All three snapshot IDs remained distinct and available. Separate real restores reproduced versions 1, 2 and 3 exactly; each target contained only its three expected files.");
        ctx.Report.RecordNote("What this proves and its limits",
            "Both restic versions actually ran against one disposable local repository. Four restores checked exact file contents, including historical bytes that differ from the current source. This tests engine/data continuity, not the updater, cloud credentials or recovery after a power failure.");
    }

    private static Dictionary<string, byte[]> ContentsForVersion(int version) => new()
    {
        ["document.txt"] = Encoding.UTF8.GetBytes($"Version {version}: " + new string((char)('A' + version), version * 29)),
        ["records [old]/receipt.txt"] = Encoding.UTF8.GetBytes($"Receipt version {version}: " + new string('x', version * 17)),
        ["unchanged.bin"] = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(),
    };

    private static void WriteSource(string source, IReadOnlyDictionary<string, byte[]> contents, int version)
    {
        foreach (var (relative, bytes) in contents)
        {
            var path = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 1, 1, 0, version, 0, DateTimeKind.Utc));
        }
    }

    private static async Task RestoreAndCompare(ResticEngine engine, Backup backup, string source,
        Destination destination, string snapshotId, int revision, string target,
        IReadOnlyDictionary<string, byte[]> expected, CancellationToken ct)
    {
        var outcome = await engine.RestoreFilesAsync(backup, source, destination, "default", revision,
            new[] { "/" }, target, null, ct, expected.Keys.ToArray(), snapshotId);
        Assert.Equal(RestoreTerminalStatus.Success, outcome.Status);
        Assert.Equal(expected.Count, outcome.FilesRestored);
        var actual = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(target, path).Replace('\\', '/')).OrderBy(path => path).ToArray();
        Assert.Equal(expected.Keys.OrderBy(path => path), actual);
        foreach (var (relative, bytes) in expected)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(target, relative), ct));
        Assert.Empty(Directory.GetDirectories(target, ".backspace-restore-*"));
    }

    private static async Task<string> ReadVersion(string executable, CancellationToken ct, params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not read an engine version.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch { }
            throw;
        }
        Assert.Equal(0, process.ExitCode);
        var lines = ((await stdout) + "\n" + (await stderr)).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var version = lines.LastOrDefault(line => line.StartsWith("restic", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(version), "The engine did not print its version.");
        return version!;
    }
}
