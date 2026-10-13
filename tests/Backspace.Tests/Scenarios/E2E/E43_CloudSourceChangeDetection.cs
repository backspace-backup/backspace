using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Backspace.Services.WinFsp;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Architecture experiment for B035. The filesystem driver and backup engines
/// are real; the remote files are an in-memory provider. This is not a cloud
/// integration test or a shipped online-only backup feature.
/// </summary>
[Collection(ScenarioCollection.Name)]
[SupportedOSPlatform("windows")]
public sealed class E43_CloudSourceChangeDetection : BackupE2EBase
{
    public E43_CloudSourceChangeDetection(ITestOutputHelper output) : base(output) { }
    protected override string ScenarioName => "E43-cloud-source-change-detection";
    [AvaloniaFact] public async Task Run_E43() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        if (!OperatingSystem.IsWindows() || !WinFspRuntimeDetector.IsInstalled())
        {
            ctx.Skip("The cloud-source architecture experiment requires Windows and an installed WinFSP runtime.");
            return;
        }
        var oldBytes = new byte[256 * 1024];
        new Random(43026).NextBytes(oldBytes);
        var newBytes = oldBytes.ToArray();
        newBytes[123] ^= 0xff; // Same length, different contents.
        var unchangedBytes = new byte[192 * 1024];
        new Random(43027).NextBytes(unchangedBytes);
        var originalDate = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var dest = new Destination { Name = "Disposable source experiment", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ctx.MakeLocalDestination("repo"), Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew("e43", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Cloud-source change detection experiment", UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), CheckSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } } };
        ServiceLocator.Config.Update(c => { c.Destinations.Add(dest); c.Backups.Add(backup); });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        string? stableRoot = null;

        await Round("First backup", oldBytes, originalDate, expectedChangedReads: true, expectedUnchangedReads: true, expectedRecovered: oldBytes);
        await Round("Unchanged backup", oldBytes, originalDate, expectedChangedReads: false, expectedUnchangedReads: false, expectedRecovered: oldBytes);
        // Negative control: ordinary Windows size/date detection misses this
        // changed revision. Prove the risk instead of assuming it from source.
        await Round("Same-size same-date change without revision signal", newBytes, originalDate,
            expectedChangedReads: false, expectedUnchangedReads: false, expectedRecovered: oldBytes);
        await Round("Changed revision with a distinct virtual timestamp", newBytes, originalDate.AddTicks(1),
            expectedChangedReads: true, expectedUnchangedReads: false, expectedRecovered: newBytes);
        ctx.Report.RecordNote("Scope and remaining work",
            "Real WinFSP, Backrest and restic; simulated remote metadata and bytes. Each round remounts to remove filesystem-cache reuse. " +
            "No Dropbox account or original placeholder was accessed. This proves the change-detection mechanism only. " +
            "Production still needs provider authorization, revision-aware persisted metadata, original-date recovery, " +
            "a bounded temporary download cache, backup/restore integration and interface controls.");

        async Task Round(string name, byte[] changed, DateTime virtualDate,
            bool expectedChangedReads, bool expectedUnchangedReads, byte[] expectedRecovered)
        {
            var provider = new CountingSource(changed, unchangedBytes, virtualDate, originalDate);
            await using var mount = new WinFspMountCoordinator(provider, openExplorer: false);
            var mounted = await mount.MountAsync(deadline.Token);
            Assert.True(mounted.Succeeded, mounted.Message);
            var root = mounted.DriveLetter + "\\";
            stableRoot ??= root;
            Assert.Equal(stableRoot, root); // Restic needs the same logical path across snapshots.
            backup.SourcePaths.Clear();
            backup.SourcePaths.Add(root);
            var run = await fixture.Engine.BackupPruneCheckAsync(backup, root, dest, "default", deadline.Token);
            Assert.True(run.Status == BackupRunStatus.Success, string.Join(" | ", run.Errors));
            Assert.Equal(expectedChangedReads, provider.ChangedReads > 0);
            Assert.Equal(expectedUnchangedReads, provider.UnchangedReads > 0);
            var changedReads = provider.ChangedReads;
            var unchangedReads = provider.UnchangedReads;
            var target = ctx.MakeRestoreTarget("round-" + Guid.NewGuid().ToString("N"));
            var recovered = await RestoreEverythingAsync(backup, root, dest, "default", target, deadline.Token);
            Assert.Equal(RestoreTerminalStatus.Success, recovered.TerminalStatus);
            Assert.Equal(2, recovered.RestoredCount);
            Assert.Equal(expectedRecovered, File.ReadAllBytes(Path.Combine(target, "changes.bin")));
            Assert.Equal(unchangedBytes, File.ReadAllBytes(Path.Combine(target, "unchanged.bin")));
            Assert.Equal(2, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
            ctx.Report.RecordNote(name,
                $"Backup content opens: changes.bin={changedReads}, unchanged.bin={unchangedReads}. " +
                "Restoring the new snapshot recovered both complete files with the expected bytes. " +
                (name.StartsWith("Same-size", StringComparison.Ordinal)
                    ? "The negative control deliberately recovered the OLD bytes: this is the defect revision tracking must prevent."
                    : "All expected byte comparisons passed."));
        }
    }

    private sealed class CountingSource(byte[] changed, byte[] unchanged, DateTime changedDate, DateTime originalDate) : ISnapshotTreeProvider
    {
        private int _changedReads, _unchangedReads;
        public int ChangedReads => Volatile.Read(ref _changedReads);
        public int UnchangedReads => Volatile.Read(ref _unchangedReads);
        private WebDavEntry[] Entries => new[] {
            new WebDavEntry("changes.bin", false, changed.Length, changedDate),
            new WebDavEntry("unchanged.bin", false, unchanged.Length, originalDate) };
        public Task<IReadOnlyList<WebDavEntry>?> ListAsync(string relativeDir, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WebDavEntry>?>(relativeDir == "" ? Entries : null);
        public Task<WebDavEntry?> StatAsync(string relativePath, CancellationToken ct) =>
            Task.FromResult(relativePath == "" ? new WebDavEntry("", true, 0, originalDate) :
                Entries.FirstOrDefault(e => e.RelativePath == relativePath));
        public Task<Stream> OpenAsync(string relativePath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (relativePath == "changes.bin")
            {
                Interlocked.Increment(ref _changedReads);
                return Task.FromResult<Stream>(new MemoryStream(changed, writable: false));
            }
            if (relativePath == "unchanged.bin")
            {
                Interlocked.Increment(ref _unchangedReads);
                return Task.FromResult<Stream>(new MemoryStream(unchanged, writable: false));
            }
            throw new FileNotFoundException(relativePath);
        }
    }
}
