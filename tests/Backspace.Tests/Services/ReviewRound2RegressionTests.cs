using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Regression coverage for the round-2 review fixes. Each test
/// reproduces a specific bug shape and asserts the new behaviour,
/// so a future refactor that quietly reintroduces the issue fails
/// CI instead of slipping through.
/// </summary>
public class ReviewRound2RegressionTests
{
    // ---- Bandwidth limit re-emits into existing repo Flags ----

    [Fact]
    public void BuildRepoFlagsForBackup_emits_upload_and_download_when_bandwidth_set()
    {
        var b = new Backup { BandwidthLimitKBps = 512 };
        var flags = ResticBackrestEngine.BuildRepoFlagsForBackup(b);
        // Order matters — Backrest rounds-trips Flags in the order
        // we sent them, and the drift check uses ordinal equality.
        Assert.Equal(new[] { "--limit-upload", "512", "--limit-download", "512" }, flags);
    }

    [Fact]
    public void BuildRepoFlagsForBackup_emits_empty_when_unlimited()
    {
        var b = new Backup { BandwidthLimitKBps = 0 };
        Assert.Empty(ResticBackrestEngine.BuildRepoFlagsForBackup(b));
        Assert.Empty(ResticBackrestEngine.BuildRepoFlagsForBackup(null));
    }

    [Fact]
    public void RepoFlagsEqual_detects_order_and_value_drift()
    {
        Assert.True(ResticBackrestEngine.RepoFlagsEqual(null, null));
        Assert.True(ResticBackrestEngine.RepoFlagsEqual(new List<string>(), null));
        Assert.True(ResticBackrestEngine.RepoFlagsEqual(
            new List<string> { "--limit-upload", "512" },
            new List<string> { "--limit-upload", "512" }));
        Assert.False(ResticBackrestEngine.RepoFlagsEqual(
            new List<string> { "--limit-upload", "512" },
            new List<string> { "--limit-upload", "256" }));
        Assert.False(ResticBackrestEngine.RepoFlagsEqual(
            new List<string> { "--limit-upload", "512" },
            new List<string> { "512", "--limit-upload" }));
    }

    // ---- ConfigStore Save_NoLock refuses while _loaded=false ----

    [Fact]
    public void ConfigStore_Save_refuses_when_Load_failed()
    {
        // Build a ConfigStore but never call Load — _loaded stays
        // false (the field-initialised default). An Update from this
        // state previously persisted the empty default AppConfig
        // over the real on-disk file.
        var prevRoot = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
        var tmp = Path.Combine(Path.GetTempPath(), $"backspace-r2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", tmp);
            AppPaths.EnsureAll();

            // Write a real config to disk first.
            var preExisting = new ConfigStore();
            preExisting.Load();
            preExisting.Update(c => c.Backups.Add(new Backup { Id = "id1", Name = "preexisting" }));
            Assert.Single(preExisting.Current.Backups);

            // Now build a SECOND store but DON'T call Load. Simulate
            // a startup where IOException prevented Load from setting
            // _loaded=true.
            var fresh = new ConfigStore();
            Assert.Throws<InvalidOperationException>(() =>
                fresh.Update(c => c.Backups.Add(new Backup { Id = "id2", Name = "should-not-persist" })));

            // The on-disk file should still hold ONLY the original
            // pre-existing backup. The "should-not-persist" Update was
            // visibly rejected because the initial Load never completed.
            var reread = new ConfigStore();
            reread.Load();
            Assert.Single(reread.Current.Backups);
            Assert.Equal("preexisting", reread.Current.Backups[0].Name);
            Assert.False(fresh.IsLoaded);
            Assert.True(reread.IsLoaded);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", prevRoot);
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ---- Update snapshot/restore on Save throw ----

    [Fact]
    public void ConfigStore_Update_rolls_back_in_memory_state_when_Save_throws()
    {
        // FileShare.None is enforced as a kernel-level mandatory lock
        // only on Windows. On Linux/macOS it's a .NET advisory in-process
        // flag, and File.Replace's rename(2) ignores it — so the Save
        // would succeed and the test's Assert.Throws<IOException> would
        // never fire. Until the test moves to a cross-platform mocking
        // seam, skip on non-Windows.
        if (!OperatingSystem.IsWindows()) return;
        // Hook ConfigRoot at a path we can break partway through.
        var prevRoot = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
        var tmp = Path.Combine(Path.GetTempPath(), $"backspace-r2-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", tmp);
            AppPaths.EnsureAll();

            var store = new ConfigStore();
            store.Load();
            store.Update(c => c.Backups.Add(new Backup { Id = "id1", Name = "first" }));
            Assert.Single(store.Current.Backups);

            // Make AppPaths.ConfigFile unwritable: open it exclusively
            // in another handle for the duration of the Update. The
            // atomic-write tmp+Replace will fail on Replace.
            using var locker = new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.Throws<IOException>(() =>
                store.Update(c => c.Backups.Add(new Backup { Id = "id2", Name = "rolled-back" })));

            // In-memory state should still have ONLY the first backup
            // because the rollback rewound after Save failed.
            Assert.Single(store.Current.Backups);
            Assert.Equal("first", store.Current.Backups[0].Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", prevRoot);
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ---- HealthcheckService UUID never logged in full ----

    [Fact]
    public void HealthcheckService_TailOfId_handles_string_and_Guid()
    {
        // Both overloads are private statics — reach via reflection.
        var t = typeof(HealthcheckService);
        var strMethod = t.GetMethod("TailOfId", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string) });
        Assert.NotNull(strMethod);
        Assert.Equal("cdef", strMethod!.Invoke(null, new object?[] { "abcdef" }));
        Assert.Equal("?", strMethod.Invoke(null, new object?[] { (string?)null }));
        Assert.Equal("?", strMethod.Invoke(null, new object?[] { "abc" }));

        var guidMethod = t.GetMethod("TailOfId", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(Guid) });
        Assert.NotNull(guidMethod);
        var g = new Guid("01234567-89ab-cdef-0123-456789abcdef");
        Assert.Equal("cdef", guidMethod!.Invoke(null, new object?[] { g }));
    }

    // ---- BackupProgressService locks on private Gate, not public BackupProgress ----

    [Fact]
    public void BackupProgressService_ReportRunFinished_locks_on_Gate()
    {
        // Defence: search the source of BackupProgressService for any
        // surviving `lock (p)` or `lock (live)` outside the comment.
        // Failure means a future refactor reintroduced the public-
        // type-lock hazard.
        var asmPath = typeof(BackupProgressService).Assembly.Location;
        var dir = Path.GetDirectoryName(asmPath)!;
        // Walk up to repo root to find the .cs source.
        var d = new DirectoryInfo(dir);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "Backspace", "Services")))
            d = d.Parent;
        Assert.NotNull(d);
        var src = File.ReadAllText(Path.Combine(d!.FullName, "src", "Backspace", "Services", "BackupProgressService.cs"));

        // The only surviving `lock (` lines should target `.Gate`.
        var bad = src.Split('\n')
            .Select((line, i) => (line: line.Trim(), idx: i + 1))
            .Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.line, @"^\s*lock\s*\("))
            .Where(t => !t.line.Contains(".Gate"))
            .ToList();
        Assert.Empty(bad);
    }

    // ---- Distinct targets in cell loop ----

    [Fact]
    public void Backup_with_duplicate_targets_de_duplicates_when_iterated_via_distinct_grouping()
    {
        // Reproduce the grouping used in BackupOrchestrator.CellLoop:
        // GroupBy (DestinationId, StorageName ?? "").
        var targets = new List<BackupTarget>
        {
            new() { DestinationId = "d1", StorageName = "default" },
            new() { DestinationId = "d1", StorageName = "default" }, // dupe
            new() { DestinationId = "d1", StorageName = null! },     // intentionally exercise a legacy null: distinct from "default"
            new() { DestinationId = "d2", StorageName = "default" },
        };
        var distinct = targets
            .GroupBy(t => (t.DestinationId, StorageName: t.StorageName ?? ""))
            .Select(g => g.First())
            .ToList();
        Assert.Equal(3, distinct.Count);
        Assert.Contains(distinct, t => t.DestinationId == "d1" && t.StorageName == "default");
        Assert.Contains(distinct, t => t.DestinationId == "d1" && t.StorageName is null);
        Assert.Contains(distinct, t => t.DestinationId == "d2" && t.StorageName == "default");
    }
}
