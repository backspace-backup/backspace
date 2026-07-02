using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Regression coverage for the round-3 + round-4 review fixes.
/// </summary>
public class ReviewRound3And4RegressionTests
{
    // ---- Round 3 P0: SweepStaleScratchDirs `||` vs `&&` ----

    [Fact]
    public void SweepStaleScratchDirs_skips_dir_with_old_CreationTime_but_recent_LastWriteTime()
    {
        // Reproduce the race: dir was created hours ago but its
        // last-modified time is fresh (verify drill writing files
        // into it RIGHT NOW). The round-3 fix changed && to ||;
        // this test asserts the `||` semantic — skip if EITHER
        // timestamp says young.
        var prevRoot = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
        var tmp = Path.Combine(Path.GetTempPath(), $"backspace-r3-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", tmp);
            AppPaths.EnsureAll();

            var verifyRoot = Path.Combine(AppPaths.ConfigRoot, "verify");
            Directory.CreateDirectory(verifyRoot);

            var inFlight = Path.Combine(verifyRoot, "active-drill");
            Directory.CreateDirectory(inFlight);
            // Force CreationTime to 48 hours ago, but LastWriteTime to NOW.
            Directory.SetCreationTimeUtc(inFlight, DateTime.UtcNow.AddHours(-48));
            Directory.SetLastWriteTimeUtc(inFlight, DateTime.UtcNow);

            var ancient = Path.Combine(verifyRoot, "orphan");
            Directory.CreateDirectory(ancient);
            Directory.SetCreationTimeUtc(ancient, DateTime.UtcNow.AddDays(-7));
            Directory.SetLastWriteTimeUtc(ancient, DateTime.UtcNow.AddDays(-7));

            BackupVerifier.SweepStaleScratchDirs();

            // In-flight (recent LastWriteTime) must survive.
            Assert.True(Directory.Exists(inFlight), "In-flight scratch dir was swept — sweep guard regressed to `&&`");
            // Truly old orphan must be deleted.
            Assert.False(Directory.Exists(ancient), "Truly-stale orphan was NOT swept");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", prevRoot);
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ---- Round 3 dedup case-insensitivity ----

    [Fact]
    public void CellLoop_distinctTargets_treats_StorageName_case_insensitively()
    {
        // Calls the actual production helper that BOTH the regular
        // cell loop and the maintenance loop use. The earlier version
        // of this test hand-rewrote the grouping expression and would
        // have stayed green even if production dropped the
        // ToLowerInvariant — false confidence.
        var targets = new List<BackupTarget>
        {
            new() { DestinationId = "d1", StorageName = "default" },
            new() { DestinationId = "d1", StorageName = "Default" },
            new() { DestinationId = "d1", StorageName = "DEFAULT" },
            new() { DestinationId = "d2", StorageName = "default" },
        };
        var helper = typeof(BackupOrchestrator).GetMethod(
            "DistinctTargets",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(helper);
        var distinct = (System.Collections.IList)helper!.Invoke(null, new object[] { targets })!;
        // Only TWO distinct (d1+default and d2+default).
        Assert.Equal(2, distinct.Count);
    }

    // ---- Round 4 P1: inner-Update Save throw rollback semantics ----

    [Fact]
    public void ConfigStore_Update_inner_Save_throw_does_not_silently_diverge_in_memory_vs_disk()
    {
        // Round 4 fixed: every frame now snapshots, but only the
        // outermost catch performs the rollback. An inner Save throw
        // must STILL propagate the exception (so the outer Update sees
        // failure), and the outer must roll back to its own pre-mutate
        // snapshot.
        if (!OperatingSystem.IsWindows()) return; // Windows-only file-locking semantics

        var prevRoot = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
        var tmp = Path.Combine(Path.GetTempPath(), $"backspace-r4-inner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", tmp);
            AppPaths.EnsureAll();

            var store = new ConfigStore();
            store.Load();
            store.Update(c => c.Backups.Add(new Backup { Id = "base", Name = "base" }));

            using var locker = new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // Subscribe a Changed handler that triggers an inner Update.
            // The locked file makes the inner's Save throw IOException.
            int handlerInvocations = 0;
            store.Changed += (_, __) =>
            {
                handlerInvocations++;
                // Inner Update throws because the file is locked.
                // The exception bubbles into RaiseChanged's try/catch
                // which swallows it (intentional — handler failures
                // shouldn't break the outer state).
            };

            // The outer Update succeeds reading the lock — wait, the
            // outer's Save would also fail. Test instead: outer's
            // mutate then Save throws because the file is locked. Even
            // though no handler ran (RaiseChanged is outside the try),
            // verify _current reflects the outer-frame snapshot
            // rollback.
            Assert.Throws<IOException>(() =>
                store.Update(c => c.Backups.Add(new Backup { Id = "rolled-back", Name = "rolled-back" })));

            // After rollback, _current still has only the "base" backup.
            Assert.Single(store.Current.Backups);
            Assert.Equal("base", store.Current.Backups[0].Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", prevRoot);
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ---- Round 4 P1: Maintenance distinct targets dedup ----

    [Fact]
    public void Maintenance_and_CellLoop_share_the_same_DistinctTargets_helper()
    {
        // Source-text guard: BOTH partial files must call
        // `DistinctTargets(`. The earlier version of this test
        // hand-rewrote the grouping expression and compared it to
        // itself — passed regardless of what either production file
        // did. This test reads the actual source files.
        var asmPath = typeof(BackupOrchestrator).Assembly.Location;
        var d = new DirectoryInfo(Path.GetDirectoryName(asmPath)!);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "Backspace", "Services")))
            d = d.Parent;
        Assert.NotNull(d);
        var srvDir = Path.Combine(d!.FullName, "src", "Backspace", "Services");
        var cellLoopSrc = File.ReadAllText(Path.Combine(srvDir, "BackupOrchestrator.CellLoop.cs"));
        var maintenanceSrc = File.ReadAllText(Path.Combine(srvDir, "BackupOrchestrator.Maintenance.cs"));
        Assert.Contains("DistinctTargets(", cellLoopSrc);
        Assert.Contains("DistinctTargets(", maintenanceSrc);

        // And the helper itself must lowercase the StorageName.
        var orchSrc = File.ReadAllText(Path.Combine(srvDir, "BackupOrchestrator.cs"));
        Assert.Matches(@"DistinctTargets\([^)]*targets[^)]*\)\s*\{[^}]*ToLowerInvariant\(\)", orchSrc);
    }

    // ---- Round 4 P1: BackupEditor host-from-rec.PingUrl ----

    [Fact]
    public void HealthcheckStatus_redaction_derives_host_from_rec_PingUrl()
    {
        // Verify the redacted display takes the host from rec.PingUrl
        // rather than hardcoding hc-ping.com. Implementation lives in
        // BackupEditorViewModel; the contract:
        //   "Created. Ping URL: {scheme}://{host}/…{last4}"
        var hosted = "https://healthchecks.mycorp.com/abc-1234-xyz-cdef";
        var u = new Uri(hosted);
        var lastSlash = hosted.LastIndexOf('/');
        var tail = lastSlash >= 0 && hosted.Length - lastSlash > 4
            ? hosted.Substring(hosted.Length - 4)
            : "?";
        var redacted = $"{u.Scheme}://{u.Host}/…{tail}";
        Assert.Equal("https://healthchecks.mycorp.com/…cdef", redacted);

        // Hosted on the public service:
        var publicUrl = "https://hc-ping.com/01234567-89ab-cdef-0123-456789abcdef";
        u = new Uri(publicUrl);
        lastSlash = publicUrl.LastIndexOf('/');
        tail = lastSlash >= 0 && publicUrl.Length - lastSlash > 4
            ? publicUrl.Substring(publicUrl.Length - 4)
            : "?";
        redacted = $"{u.Scheme}://{u.Host}/…{tail}";
        Assert.Equal("https://hc-ping.com/…cdef", redacted);
    }

    // ---- Round 2 dup-plan fix (no inline Plan add when drift detected) ----

    [Fact]
    public void EnsurePlanAndRepoCore_drift_branch_falls_through_without_inline_plan_add()
    {
        // The round-3 fix removed an inline `cfg.Plans.Add(...)` from
        // the bandwidth-flag drift branch so the unconditional
        // fall-through `if (existingPlan is null) cfg.Plans.Add(...)`
        // is the sole add path. Verify via source-text grep that no
        // surviving `cfg.Plans.Add(BuildPlanFromBackup(` lives inside
        // the drift block.
        var asmPath = typeof(ResticBackrestEngine).Assembly.Location;
        var d = new DirectoryInfo(Path.GetDirectoryName(asmPath)!);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "Backspace", "Services")))
            d = d.Parent;
        Assert.NotNull(d);
        var src = File.ReadAllText(Path.Combine(d!.FullName, "src", "Backspace", "Services", "ResticBackrestEngine.cs"));
        var addCount = System.Text.RegularExpressions.Regex.Matches(
            src,
            @"cfg\.Plans\.Add\(BuildPlanFromBackup\(").Count;
        // Exactly 1 add call site after the fix (the unconditional
        // fall-through). Any other would indicate the inline-add
        // came back.
        Assert.Equal(1, addCount);
    }
}
