using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Regression coverage for the wave-15..18 hardening pass. Each test
/// reproduces a specific bug shape the code USED to have and asserts
/// the new behaviour — so a future refactor that quietly reintroduces
/// the bug fails CI instead of slipping through.
/// </summary>
public class Wave18RegressionTests
{
    // ---- ConfigStore: transient IO must not be misreported as "Corrupt" ----

    [Fact]
    public void ConfigStore_Load_keepsInMemoryState_whenFileIsLockedByConcurrentWriter()
    {
        // Arrange — point ConfigRoot at an isolated temp dir + write a
        // valid config, then open the file with FileShare.None to
        // simulate an in-flight atomic-replace.
        var prevRoot = Environment.GetEnvironmentVariable("BACKSPACE_CONFIG_ROOT");
        var tmp = Path.Combine(Path.GetTempPath(), $"backspace-wave18-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", tmp);
            AppPaths.EnsureAll();

            var store = new ConfigStore();
            store.Load();
            store.Update(c => c.Backups.Add(new Backup { Id = "id1", Name = "alpha" }));
            Assert.Single(store.Current.Backups);

            // Re-open and exclusive-lock the file, simulating a concurrent
            // writer's File.Replace mid-flight.
            using (var locker = new FileStream(AppPaths.ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = new ConfigStore();
                locked.Load();
                // Should NOT have wiped the file, should NOT have created
                // a .broken.* sibling — must have logged & kept defaults.
                var brokenSiblings = Directory.GetFiles(
                    Path.GetDirectoryName(AppPaths.ConfigFile)!,
                    Path.GetFileName(AppPaths.ConfigFile) + ".broken.*");
                Assert.Empty(brokenSiblings);
            }

            // Sanity: original file still loads after the lock releases.
            var reopen = new ConfigStore();
            reopen.Load();
            Assert.Single(reopen.Current.Backups);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", prevRoot);
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    // ---- WebDAV path traversal: prefix-confusion via sibling dir ----

    [Fact]
    public void DirectorySnapshotTreeProvider_rejects_siblingPrefixConfusion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"backspace-wave18-snap-{Guid.NewGuid():N}");
        var siblingPrefix = root + "extra";
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(siblingPrefix);
            File.WriteAllText(Path.Combine(siblingPrefix, "secret.txt"), "leak");

            var provider = new DirectorySnapshotTreeProvider(root);
            // Use reflection to reach the private ResolveLocal — it's the
            // single guard that gates every WebDAV read.
            var m = typeof(DirectorySnapshotTreeProvider).GetMethod(
                "ResolveLocal", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(m);

            // Without the separator-anchored boundary, the legacy code
            // would have computed candidate=siblingPrefix+secret.txt and
            // returned it because candidate.StartsWith(root) is true.
            // The fix anchors on (root + separator) so the sibling
            // resolves as "outside" → null.
            var relative = ".." + Path.DirectorySeparatorChar
                                 + Path.GetFileName(siblingPrefix)
                                 + Path.DirectorySeparatorChar
                                 + "secret.txt";
            var resolved = m!.Invoke(provider, new object?[] { relative });
            Assert.Null(resolved);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(siblingPrefix, recursive: true); } catch { }
        }
    }

    // ---- HealthcheckService: ping_url tail-only redaction ----

    [Fact]
    public void HealthcheckService_TailOfUuid_returnsLast4_andQuestionMarkOnGarbage()
    {
        var m = typeof(HealthcheckService).GetMethod(
            "TailOfUuid", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(m);

        Assert.Equal("89ab", m!.Invoke(null, new object?[] { "https://hc-ping.com/01234567-89ab" }));
        Assert.Equal("?", m.Invoke(null, new object?[] { (string?)null }));
        Assert.Equal("?", m.Invoke(null, new object?[] { "" }));
        Assert.Equal("?", m.Invoke(null, new object?[] { "abc" }));        // no slash
        Assert.Equal("?", m.Invoke(null, new object?[] { "https://x/y" })); // suffix too short
    }

    // ---- LogStore + VssElevationCoordinator: JsonStringEnumConverter
    //      reads BOTH legacy integer AND new string forms (back-compat) ----

    [Fact]
    public void LogStoreJsonOpts_acceptIntegerEncodedEnums_andEmitStrings()
    {
        var optsField = typeof(LogStore).GetField(
            "JsonOpts", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(optsField);
        var opts = (JsonSerializerOptions)optsField!.GetValue(null)!;

        // Legacy file shape — Status as integer (Success = 2 per enum order).
        var legacy = $"{{\"status\":{(int)BackupRunStatus.Success},\"kind\":0,\"backupName\":\"a\",\"backupId\":\"i\"}}";
        var rec = JsonSerializer.Deserialize<RunRecord>(legacy, opts);
        Assert.NotNull(rec);
        Assert.Equal(BackupRunStatus.Success, rec!.Status);

        // Fresh write — Status round-trips as a STRING for human-readable logs.
        var json = JsonSerializer.Serialize(new RunRecord { Status = BackupRunStatus.Failed }, opts);
        Assert.Contains("\"Failed\"", json);
    }

    // ---- BackupOrchestrator.RunningInfo.TrySetCancelReason: first wins ----

    [Fact]
    public void RunningInfo_TrySetCancelReason_firstWriterWins_underContention()
    {
        // RunningInfo is private. Build one via reflection so the test
        // can hammer TrySetCancelReason from two threads.
        var orchType = typeof(BackupOrchestrator);
        var infoType = orchType.GetNestedType("RunningInfo", BindingFlags.NonPublic);
        Assert.NotNull(infoType);
        var ctor = infoType!.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)[0];

        // First positional arg shape: (CancellationTokenSource, DateTime, RunRecord).
        using var cts = new CancellationTokenSource();
        var info = ctor.Invoke(new object[] { cts, DateTime.UtcNow, new RunRecord() });

        var trySet = infoType.GetMethod("TrySetCancelReason")!;
        var readReason = infoType.GetProperty("CancelReason")!;

        // Hammer from two threads — exactly one should observe "true".
        var winners = 0;
        var t1 = new Thread(() => { if ((bool)trySet.Invoke(info, new object?[] { "user" })!) Interlocked.Increment(ref winners); });
        var t2 = new Thread(() => { if ((bool)trySet.Invoke(info, new object?[] { "metered" })!) Interlocked.Increment(ref winners); });
        t1.Start(); t2.Start(); t1.Join(); t2.Join();

        Assert.Equal(1, winners);
        var final = (string?)readReason.GetValue(info);
        Assert.True(final is "user" or "metered", $"unexpected winning reason: {final}");

        // Subsequent calls must be no-ops regardless of input.
        Assert.False((bool)trySet.Invoke(info, new object?[] { "later" })!);
        Assert.Equal(final, (string?)readReason.GetValue(info));

        // Empty / null inputs never win.
        Assert.False((bool)trySet.Invoke(info, new object?[] { "" })!);
        Assert.False((bool)trySet.Invoke(info, new object?[] { (string?)null })!);
    }

    // ---- Destination model: NeedsOAuth / IsLocalLike are [JsonIgnore] ----

    [Fact]
    public void Destination_NeedsOAuthAndIsLocalLike_areNotSerialized()
    {
        var d = new Destination { Id = "x", Name = "n", Kind = DestinationKind.DropboxAppScoped, PathOrSubpath = "Apps/Backspace" };
        var json = JsonSerializer.Serialize(d, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        // Both computed properties must be omitted to keep config.json tidy
        // and to avoid the round-trip-lies-back-to-user trap if either is
        // ever made a setter.
        Assert.DoesNotContain("\"needsOAuth\"", json);
        Assert.DoesNotContain("\"isLocalLike\"", json);
    }
}
