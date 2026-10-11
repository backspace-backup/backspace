using System;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Rotate a destination's storage password and prove the chain
/// stays usable. Path:
///   1. Run a backup with password P1.
///   2. Call <see cref="ResticBackrestEngine.ChangeStoragePasswordAsync"/>
///      with (P1 → P2). This is the same primitive the
///      ChangeStoragePasswordWindow uses; the dialog adds UX
///      around it but the engine surface is identical.
///   3. Re-seed the SecretsStore with P2 — what the editor's
///      ApplyChangedStoragePassword would do.
///   4. Run another backup; if P2 didn't take, the engine RPC fails
///      with an auth error.
///   5. Restore from the latest revision; if either of the two
///      revisions (one written under P1, one under P2) are
///      unrecoverable, byte-equality breaks.
///
/// This is the regression net for the "I rotated the password and
/// now I can't restore anything" class of bug.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E19_ChangeStoragePassword : BackupE2EBase
{
    public E19_ChangeStoragePassword(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E19-change-storage-password";

    [AvaloniaFact] public async Task Run_E19() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var source     = ctx.MakeSource("docs", seed: 191, targetBytes: 600_000);
        var destPath   = ctx.MakeLocalDestination("rotate-local");
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var oldPwd = $"old-{Guid.NewGuid():N}";
        var newPwd = $"new-{Guid.NewGuid():N}";
        var pwdRef = ServiceLocator.Secrets.PutNew("e19", oldPwd);

        var dest = new Destination
        {
            Name = "Local — rotate target",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = destPath,
            Encrypted = true,
            StoragePasswordRef = pwdRef,
        };
        var backup = new Backup
        {
            Name = "Docs (E19)",
            SourcePaths = { source },
            UseVss = false,
            // Non-null sentinels suppress inline prune/check.
            PruneSchedule = new BackupSchedule(),
            CheckSchedule = new BackupSchedule(),
            Threads = 2,
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } },
        };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);

        // Round 1 — backup under the old password.
        var rec1 = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(rec1.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Pre-rotation backup ended {rec1.Status}; errors={string.Join(" | ", rec1.Errors)}.");

        // Rotate. ChangeStoragePasswordAsync calls `restic key passwd`
        // against the repo's small config blob — instant regardless of
        // repo size; the master encryption key is unchanged.
        var (exit, output) = await fixture.Engine.ChangeStoragePasswordAsync(
            dest, "default", oldPwd, newPwd, CancellationToken.None);
        Assert.True(exit == 0,
            $"`restic key passwd` exited {exit}; output={output}");

        Assert.Equal(newPwd, ServiceLocator.Secrets.Get(pwdRef));
        Assert.False(ServiceLocator.Secrets.TryGet(pwdRef + ":pending-rotation", out _));
        Assert.NotEqual(0, await ReadSnapshotsWithPassword(destPath, oldPwd));
        Assert.Equal(0, await ReadSnapshotsWithPassword(destPath, newPwd));
        ctx.Snapshot(host, "after rotation", "Storage password rotated; SecretsStore updated.");
        ctx.Report.RecordNote("Password negative control", "The old password is rejected by restic; the new password opens the repository. The engine saved the new encrypted credential before releasing its maintenance lease.");

        // Round 2 — backup must succeed under the NEW password.
        var rec2 = await RunBackupViaUiAsync(ctx, backup, view, CancellationToken.None);
        Assert.True(rec2.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Post-rotation backup ended {rec2.Status}; errors={string.Join(" | ", rec2.Errors)}. " +
            $"If this says 'password incorrect' the rotation didn't reach the engine.");

        // Restore: latest revision (from round 2). The pre-rotation
        // revision (round 1) is intentionally NOT exercised here —
        // its keying is now historical, and what matters to the user
        // is that the post-rotation chain works.
        var outcome = await RestoreEverythingAsync(
            backup, source, dest, "default", restoreDir, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);
        AssertBytesEqual(source, restoreDir, ctx);

        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(backup, source, dest, "default", default);
        var oldest = revisions.Min(r => r.Number);
        var oldFiles = await ServiceLocator.Revisions.ListFilesAsync(backup, source, dest, "default", oldest, default);
        var oldTarget = ctx.MakeRestoreTarget("before-password-change");
        var oldOutcome = await ServiceLocator.Restore.RunAsync(new RestoreRequest
        {
            Backup = backup, SourcePath = source, Destination = dest, StorageName = "default",
            Revision = oldest, Files = oldFiles.Select(f => f.Path).ToArray(), TargetPath = oldTarget,
        }, default);
        Assert.Equal(RestoreTerminalStatus.Success, oldOutcome.TerminalStatus);
        foreach (var file in oldFiles)
        {
            using var original = File.OpenRead(Path.Combine(source, file.Path));
            using var restored = File.OpenRead(Path.Combine(oldTarget, file.Path));
            Assert.Equal(SHA256.HashData(original), SHA256.HashData(restored));
        }
        ctx.Report.RecordNote("Pre-rotation snapshot", $"Revision {oldest} restored after password rotation; complete SHA-256 hashes matched for all {oldFiles.Count} files, not a sample.");
    }

    private static async Task<int> ReadSnapshotsWithPassword(string repository, string password)
    {
        var info = new ProcessStartInfo(AppPaths.ResticBinary)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("snapshots");
        info.ArgumentList.Add("--json");
        info.ArgumentList.Add("--no-lock");
        info.Environment["RESTIC_REPOSITORY"] = repository;
        info.Environment["RESTIC_PASSWORD"] = password;
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode;
    }
}
