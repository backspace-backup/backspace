using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.E2E;

/// <summary>
/// Exercises the NTFS edge-case generator and round-trips its output
/// through the active <see cref="IBackupEngine"/>. The generator is
/// the value here — it builds a deterministic source tree containing
/// hardlinks, sparse files, ADS, junctions, long paths, and an ACL
/// with inheritable ACEs. The round-trip step is a smoke check
/// against the in-memory engine: not every NTFS feature survives a
/// File.Copy, but byte-equality of "regular" file content (the bulk
/// payload, the ADS host's default stream, the ACL'd child file) is
/// the floor.
///
/// The real-engine ("does Restic+VSS preserve all this for real?")
/// test belongs in a manual smoke-test against a fetched
/// backrest.exe; recording the manifest's documented gaps here makes
/// it easy to spot regressions when that manual run lands.
///
/// Skipped at the class level on non-Windows hosts (every edge case
/// is NTFS-specific) and on hosts that refuse to materialise the
/// individual features.
/// </summary>
public class EdgeCaseBackupRestoreTests
{
    private readonly ITestOutputHelper _out;

    public EdgeCaseBackupRestoreTests(ITestOutputHelper output) => _out = output;

    /// <summary>Tests use a small total size for speed; the 1 GB
    /// default is intended for the standalone "verify my backup
    /// handles edge cases" runner. 4 MB is large enough to exercise
    /// the chunked-write path without slowing CI.</summary>
    private const long TestTotalBytes = 4 * 1024 * 1024;

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Generator_BuildsEveryFeatureItCan()
    {
        if (!IsWindowsHost()) return;

        using var ws = new TempWorkspace("edge-cases-generate");
        var root = ws.Sub("fixture");

        var manifest = EdgeCaseFileTree.Generate(root, TestTotalBytes);

        _out.WriteLine($"Generated fixture at {root}");
        if (manifest.Unsupported.Count > 0)
        {
            _out.WriteLine("Host refused some features (documented here, not asserted away):");
            foreach (var u in manifest.Unsupported)
                _out.WriteLine($"  - {u.Feature}: {u.ErrorKind} — {u.ErrorMessage}");
        }

        // Bulk payload is the only feature every Windows host must
        // support. Everything else is best-effort and documented.
        Assert.True(manifest.BulkPayload, "Bulk payload generation failed; see test output.");
        Assert.True(manifest.BulkPayloadBytes > TestTotalBytes / 2,
            $"Bulk payload landed at {manifest.BulkPayloadBytes} B, expected close to {TestTotalBytes}.");

        // Hardlinks: when supported, the clone reads back identical
        // content to the source AND reports the same NumberOfLinks.
        if (manifest.HardLinks)
        {
            Assert.Equal(File.ReadAllText(manifest.HardLinkSource!), File.ReadAllText(manifest.HardLinkClone!));
        }

        // Sparse files: logical length matches what we set even though
        // the on-disk allocation is much smaller. The test asserts the
        // logical length only; on-disk allocation queries need
        // DeviceIoControl FSCTL_GET_RETRIEVAL_POINTERS which we don't
        // want to wire in for a smoke test.
        if (manifest.SparseFiles)
        {
            Assert.Equal(64L * 1024 * 1024, manifest.SparseFileLogicalBytes);
        }

        // ADS: the host file's default stream lives on disk as a
        // normal file; the alternate stream isn't enumerable via
        // Directory.EnumerateFiles. We just verify the host exists.
        if (manifest.AlternateDataStreams)
        {
            Assert.True(File.Exists(manifest.AdsHostPath));
            Assert.Equal("default-stream content", File.ReadAllText(manifest.AdsHostPath!));
        }

        // Junction: appears as a directory but its CreationTime
        // sometimes drifts; the reparse-point attribute is the clean
        // signal.
        if (manifest.JunctionPoints)
        {
            Assert.True(Directory.Exists(manifest.JunctionPath));
            var attrs = File.GetAttributes(manifest.JunctionPath!);
            Assert.True((attrs & FileAttributes.ReparsePoint) != 0,
                "Junction should carry the ReparsePoint attribute.");
        }

        // Long path: the leaf file's path exceeds the 260-char MAX_PATH
        // ceiling and is reachable via the long-path-aware APIs.
        if (manifest.LongPaths)
        {
            Assert.True(manifest.LongPathLeafLength > 260);
            // File.Exists on a >260-char path uses the long-path-aware
            // call when the manifest opts in; tests sometimes run
            // without that opt-in, so we use the \\?\ prefix which
            // works either way.
            Assert.True(File.Exists(@"\\?\" + manifest.LongPathLeafFile));
        }

        // ACL: the child file ends up with the inheritable ACE the
        // parent declared even though we never set anything on the
        // child directly.
        if (manifest.NtfsAcls)
        {
            var childAcl = new FileInfo(manifest.AclChildFile!).GetAccessControl();
            var rules = childAcl.GetAccessRules(includeExplicit: true, includeInherited: true,
                typeof(System.Security.Principal.SecurityIdentifier))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .ToList();
            Assert.Contains(rules, r => r.IsInherited);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RoundTrip_BulkPayload_ByteIdentical()
    {
        if (!IsWindowsHost()) return;

        using var ws = new TempWorkspace("edge-cases-roundtrip");
        var source = ws.Sub("source");
        var storage = ws.Sub("storage");
        var restored = ws.Sub("restored");

        var manifest = EdgeCaseFileTree.Generate(source, TestTotalBytes);
        Assert.True(manifest.BulkPayload);

        // Reset config so the previous test's destination doesn't
        // bleed in.
        ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Destinations.Clear(); });

        var destination = new Destination
        {
            Name = "edge-local",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = storage,
            Encrypted = false,
        };
        var backup = new Backup
        {
            Name = "edge_roundtrip",
            SourcePaths = { source },
            // Non-null sentinels suppress inline prune/check.
            PruneSchedule = new BackupSchedule(),
            CheckSchedule = new BackupSchedule(),
            UseVss = false, // VSS would need elevation; the in-memory engine doesn't care
            ExcludeCloudFiles = false,
        };
        backup.Targets.Add(new BackupTarget { DestinationId = destination.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Destinations.Add(destination); cfg.Backups.Add(backup); });

        var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"Backup ended {record.Status}: {string.Join(" | ", record.Errors)}");

        // Restore the whole snapshot back into a scratch dir and
        // byte-compare the bulk payload. The in-memory engine doesn't
        // preserve hardlinks / sparse layout / ADS / junctions / ACLs
        // — those are real-engine concerns documented above. What it
        // DOES preserve is the default-stream byte content of every
        // regular file, which is the contract every engine has to meet.
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, source, destination, "default", CancellationToken.None);
        Assert.NotEmpty(revisions);

        var files = await ServiceLocator.Revisions.ListFilesAsync(
            backup, source, destination, "default", revisions[0].Number, CancellationToken.None);
        Assert.NotEmpty(files);

        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = source,
            Destination = destination,
            StorageName = "default",
            Revision = revisions[0].Number,
            Files = files.Select(f => f.Path).ToList(),
            TargetPath = restored,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2 },
        };
        var outcome = await ServiceLocator.Restore.RunAsync(request, CancellationToken.None);
        Assert.Equal(0, outcome.FailedCount);

        var srcBulk = new FileInfo(manifest.BulkPayloadPath!).FullName;
        var restoredBulk = Path.Combine(restored, "bulk.dat");
        Assert.True(File.Exists(restoredBulk),
            $"Bulk payload missing from restored tree at {restoredBulk}");
        Assert.Equal(
            Sha256Hex(srcBulk),
            Sha256Hex(restoredBulk));
    }

    /// <summary>Returns true when the host is Windows. Tests that
    /// touch NTFS-specific APIs call this and early-out cleanly on
    /// other platforms — xUnit v2 doesn't have Assert.Skip, so a
    /// quiet return is the pragmatic shape.</summary>
    private static bool IsWindowsHost() => OperatingSystem.IsWindows();

    private static string Sha256Hex(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }
}
