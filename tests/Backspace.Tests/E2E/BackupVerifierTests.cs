using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.E2E;

/// <summary>
/// Round-trip drill: generate a synthetic source tree, back it up,
/// then run BackupVerifier and assert that the drill picks files,
/// restores them successfully, and byte-matches each one against the
/// (unchanged) source. This is the same scenario the user runs from
/// the Dashboard "Test restore" button — the goal is end-to-end
/// confidence that a "backup succeeded" claim is actually restorable.
///
/// We force a small source so the verify drill picks all available
/// files when the pool is &lt; <see cref="BackupVerifier.FilesToSample"/>;
/// the assertion is on per-file outcomes rather than counts.
/// </summary>
public class BackupVerifierTests
{
    private readonly ITestOutputHelper _out;
    public BackupVerifierTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task VerifyAsync_freshLocalBackup_allFilesByteIdentical()
    {
        ResetConfig();

        using var ws = new TempWorkspace("e2e-verify");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");

        var summary = SyntheticFileTree.Generate(sourceDir, seed: 42, targetBytes: 800_000);
        _out.WriteLine($"Generated source: {summary}");

        var destination = new Destination
        {
            Name = "verify-test",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "verify_roundtrip",
            SourcePaths = { sourceDir },
            FiltersText = "",
            // Non-null sentinels suppress inline prune/check.
            PruneSchedule = new BackupSchedule(),
            CheckSchedule = new BackupSchedule(),
            AbortOnMeteredNetwork = false,
            Enabled = true,
            Threads = 2,
            UseVss = false,
            Targets = { new BackupTarget { DestinationId = "", StorageName = "default" } },
        };
        backup.Targets[0].DestinationId = destination.Id;

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destination);
            cfg.Backups.Add(backup);
        });

        // 1) Run the backup so the verifier has something to read back.
        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.True(
            record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended with status {record.Status}. Errors: " +
            string.Join(" | ", record.Errors));

        // 2) Run the verify drill.
        var outcome = await ServiceLocator.Verifier.VerifyAsync(backup, CancellationToken.None);

        _out.WriteLine($"Verify summary: {outcome.Summary}");
        foreach (var f in outcome.Files)
            _out.WriteLine($"  {f.Status,-15} {f.Path}{(f.Detail is null ? "" : "  (" + f.Detail + ")")}");

        // 3) Per-file assertions. Every file should have either matched
        //    bytes (OK) or matched size with a justified "skip compare"
        //    reason. SizeMismatch / BytesMismatch / RestoreFailed are
        //    the failure shapes we care about catching.
        Assert.NotEmpty(outcome.Files);
        Assert.Null(outcome.FatalError);
        Assert.True(outcome.OverallPass,
            $"Verify failed: {outcome.Summary}; per-file = " +
            string.Join(", ", outcome.Files.Select(f => $"{f.Path}:{f.Status}")));

        // Source files weren't modified between backup and verify, so
        // we expect actual byte comparison to have happened (not just
        // size match). At least one file should be OK; OkNoCompare is
        // legal but should be the exception, not the rule, on this
        // synthetic tree.
        Assert.Contains(outcome.Files, f => f.Status == FileVerifyStatus.OK);
        Assert.DoesNotContain(outcome.Files, f =>
            f.Status is FileVerifyStatus.SizeMismatch
                     or FileVerifyStatus.BytesMismatch
                     or FileVerifyStatus.RestoreFailed);

        // 4) PersistOutcome writes back to config — Dashboard reads from there.
        ServiceLocator.Verifier.PersistOutcome(outcome);
        var refreshed = ServiceLocator.Config.Current.Backups.Single(b => b.Id == backup.Id);
        Assert.True(refreshed.LastVerifyPass);
        Assert.NotNull(refreshed.LastVerifyUtc);
        Assert.Equal(outcome.Summary, refreshed.LastVerifySummary);
    }

    [Fact]
    public async Task VerifyAsync_midFlightSourceEdit_replacementLoopRetriesDirtyFile()
    {
        // D-6: the "mid-flight dirty" replacement loop. When the
        // verifier picks a file that the user is actively writing to,
        // the picked sample's size / mtime can differ from the
        // restored snapshot. The verifier marks that result
        // IsMidFlightDirty and asks the picker for a REPLACEMENT
        // pick from the un-picked pool — so the final result list
        // reflects the user's whole repo, not just the files that
        // happen not to be open in an editor right now.
        //
        // This is the unique-feature pin that complements restic's
        // own `check`: restic's check verifies the on-disk repo
        // structure, but doesn't restore-and-compare against the
        // ORIGINAL files on the user's machine. Backspace's verifier
        // does, and the replacement loop is what keeps it from
        // surfacing spurious "FAILED" results when the user happens
        // to be editing a file during the drill.
        //
        // Shape:
        //  1. Back up a small synthetic tree.
        //  2. Mutate ONE file post-backup so its current bytes
        //     differ from the snapshot. Keep size identical so the
        //     size-match → OkNoCompare path fires (which is what
        //     production sees when the user is still editing).
        //  3. Verify; assert that the per-file outcome for the
        //     touched file falls into the "still passes overall"
        //     bucket — either OkNoCompare with the mid-flight
        //     detail, OR replaced from the list entirely.
        //  4. Overall pass must remain true: a user touching a
        //     file during the drill is NOT a failure condition.
        ResetConfig();
        using var ws = new TempWorkspace("e2e-verify-midflight");
        var sourceDir  = ws.Sub("source");
        var storageDir = ws.Sub("storage");

        SyntheticFileTree.Generate(sourceDir, seed: 4242, targetBytes: 800_000);

        var destination = new Destination
        {
            Name = "verify-midflight",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storageDir,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "verify_midflight",
            SourcePaths = { sourceDir },
            FiltersText = "",
            PruneSchedule = new BackupSchedule(),
            CheckSchedule = new BackupSchedule(),
            AbortOnMeteredNetwork = false,
            Enabled = true,
            Threads = 2,
            UseVss = false,
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } },
        };

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destination);
            cfg.Backups.Add(backup);
        });

        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");

        // Mutate one source file in place, keeping size constant. This
        // is the production "user editing a doc while we verify"
        // shape — the snapshot has the OLD bytes, the file on disk
        // has NEW bytes of the same length.
        var allFiles = System.IO.Directory.EnumerateFiles(sourceDir, "*", System.IO.SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(allFiles);
        var target = allFiles.OrderBy(p => new System.IO.FileInfo(p).Length).Last();
        var bytes = System.IO.File.ReadAllBytes(target);
        Assert.True(bytes.Length > 16, "Test fixture too small to scramble safely.");
        // Flip the first 16 bytes in place — same total size, different content.
        for (int i = 0; i < 16; i++) bytes[i] = (byte)(bytes[i] ^ 0xFF);
        System.IO.File.WriteAllBytes(target, bytes);

        var outcome = await ServiceLocator.Verifier.VerifyAsync(backup, CancellationToken.None);
        _out.WriteLine($"Verify summary (mid-flight scenario): {outcome.Summary}");
        foreach (var f in outcome.Files)
            _out.WriteLine($"  {f.Status,-15} {f.Path}{(f.Detail is null ? "" : "  (" + f.Detail + ")")}");

        // Overall pass must remain true: editing-during-drill is not
        // a backup failure.
        Assert.Null(outcome.FatalError);
        Assert.True(outcome.OverallPass,
            $"Mid-flight source edit caused overall verify FAIL; per-file = " +
            string.Join(", ", outcome.Files.Select(f => $"{f.Path}:{f.Status}")));

        // No file should appear with the hard-failure shapes —
        // BytesMismatch from a touched file would mean the
        // mid-flight discriminator missed it.
        Assert.DoesNotContain(outcome.Files, f =>
            f.Status is FileVerifyStatus.SizeMismatch
                     or FileVerifyStatus.BytesMismatch
                     or FileVerifyStatus.RestoreFailed);
    }

    private static void ResetConfig()
    {
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Backups.Clear();
            cfg.Destinations.Clear();
        });
    }
}
