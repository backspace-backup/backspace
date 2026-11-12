using System;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Verifies the parts of <see cref="ResticEngine"/> that don't
/// need restic: deterministic ID generation, the command lines built from
/// a Backspace Backup/Destination, restore output parsing, and a missing
/// restic reported as a missing component.
/// </summary>
public class ResticEngineTests
{
    [Fact]
    public void MakeRepoId_IsDeterministicForSameDestinationAndStorage()
    {
        var dest = new Destination { Id = "dest-1", Name = "Primary", Kind = DestinationKind.LocalFolder, PathOrSubpath = "C:/tmp/x" };

        var a = ResticEngine.MakeRepoId(dest, "default");
        var b = ResticEngine.MakeRepoId(dest, "default");

        Assert.Equal(a, b);
    }

    [Fact]
    public void MakeRepoId_DiffersByStorageName()
    {
        var dest = new Destination { Id = "dest-1", Name = "Primary", Kind = DestinationKind.LocalFolder, PathOrSubpath = "C:/tmp/x" };

        var a = ResticEngine.MakeRepoId(dest, "default");
        var b = ResticEngine.MakeRepoId(dest, "alternate");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MakeRepoId_DefaultsEmptyStorageNameToDefaultSentinel()
    {
        var dest = new Destination { Id = "d", Name = "x", Kind = DestinationKind.LocalFolder, PathOrSubpath = "/tmp" };

        var named = ResticEngine.MakeRepoId(dest, "default");
        var empty = ResticEngine.MakeRepoId(dest, "");

        Assert.Equal(named, empty);
    }

    [Fact]
    public void MakeRepoId_ProducesSafeSlug()
    {
        // Repo ids keep their shape (they appear in
        // logs and key the repository's turn): lower-kebab, no spaces,
        // no slashes.
        var dest = new Destination { Id = "DEST 1 / awkward", Name = "X", Kind = DestinationKind.LocalFolder, PathOrSubpath = "/tmp" };
        var id = ResticEngine.MakeRepoId(dest, "DEFAULT");

        Assert.DoesNotContain(' ', id);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('\\', id);
        Assert.Equal(id, id.ToLowerInvariant());
    }

    [Fact]
    public void MakePlanId_DiffersBySourcePath()
    {
        var backup = new Backup { Id = "bk-1", Name = "Photos" };
        var dest = new Destination { Id = "dest-1", Name = "Primary", Kind = DestinationKind.LocalFolder, PathOrSubpath = "/tmp" };

        var a = ResticEngine.MakePlanId(backup, "C:/Users/me/Photos", dest, "default");
        var b = ResticEngine.MakePlanId(backup, "C:/Users/me/Documents", dest, "default");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MakePlanId_StableForSameInputs()
    {
        var backup = new Backup { Id = "bk-1", Name = "Photos" };
        var dest = new Destination { Id = "dest-1", Name = "Primary", Kind = DestinationKind.LocalFolder, PathOrSubpath = "/tmp" };

        var a = ResticEngine.MakePlanId(backup, "C:/Users/me/Photos", dest, "default");
        var b = ResticEngine.MakePlanId(backup, "C:/Users/me/Photos", dest, "default");

        Assert.Equal(a, b);
    }

    [Fact]
    public void The_backup_command_holds_the_one_source_path()
        => Assert.Equal(new[] { "backup", "--json", "/data/photos" }, ResticEngine.BackupArguments("/data/photos"));

    [Fact]
    public void The_filters_text_reaches_restic_as_an_exclude_file()
    {
        // U151: restic gets the Filters text as written, in a file; the
        // command carries no rules of its own.
        var backup = new Backup
        {
            Id = "bk-1",
            Name = "Photos",
            FiltersText = "*.tmp\nnode_modules/\n# comment\n\n",
        };

        var trailing = ResticEngine.BackupTrailing(backup, "p", null);

        Assert.DoesNotContain(trailing, t => t.Contains("node_modules", System.StringComparison.Ordinal));
        Assert.Equal(new[] { FilterTranslator.ExcludeFileFlag, ResticEngine.ExcludeFilePath("p") }, trailing.TakeLast(2));
    }

    /// <summary>U133: restic gets the rules as written. A repeated rule is
    /// sent once, at its last place: restic's last matching rule decides, so
    /// keeping the first copy would put "a" before "!b" and back up a path
    /// both match.</summary>
    [Fact]
    public void Repeated_rules_are_sent_once_at_their_last_place()
    {
        Assert.Equal(["!b", "a"], ResticEngine.WithoutRepeats(["a", "!b", "a"]));
        Assert.Equal(["a", "!b"], ResticEngine.WithoutRepeats(["a", "a", "!b"]));
        Assert.Equal(["C:/**/*.tmp", "c:/**/*.TMP"], ResticEngine.WithoutRepeats(["C:/**/*.tmp", "c:/**/*.TMP"]));
        Assert.NotEqual(["a", "!b"], ResticEngine.WithoutRepeats(["a", "!b", "a"]));
    }

    [Fact]
    public void ResticUriFor_LocalFolderReturnsRawPath()
    {
        // Restic's local-filesystem repo URI IS the path; no scheme.
        var dest = new Destination { Id = "d", Kind = DestinationKind.LocalFolder, PathOrSubpath = "C:/Backups/me" };
        Assert.Equal("C:/Backups/me", ResticEngine.ResticUriFor(dest));
    }

    [Fact]
    public void ResticUriFor_CloudKindEmitsRcloneUri()
    {
        // Cloud destinations resolve to real rclone:<remote>:<path>
        // URIs via ResticUriResolver. Pin the shape here; the
        // resolver's own tests cover the wire-format details.
        var dest = new Destination { Id = "abcdef01", Kind = DestinationKind.DropboxAppScoped, PathOrSubpath = "Apps/backspace" };
        var uri = ResticEngine.ResticUriFor(dest);
        Assert.StartsWith("rclone:dropbox-app-", uri);
        Assert.EndsWith(":Apps/backspace", uri);
    }

    [Fact]
    public void ParseResticRestoreSummary_PicksFinalSummaryLine_AndZeroErrorsMeansSuccess()
    {
        // Real restic 0.18.1 --json output: status lines, then ONE
        // summary line at the end. Our parser must walk every line,
        // skip non-JSON / non-summary entries, and pick out the last
        // summary's structured counts. The zero-errors case is the
        // load-bearing one: it lets us reclassify a restic exit-1
        // (metadata-only warning) back to Success.
        var output = string.Join("\n", new[]
        {
            "{\"message_type\":\"status\",\"seconds_elapsed\":0,\"percent_done\":0.0}",
            "{\"message_type\":\"verbose_status\",\"action\":\"restored\",\"item\":\"/file1\"}",
            "{\"message_type\":\"summary\",\"seconds_elapsed\":3,\"total_files\":42,\"files_restored\":42,\"files_skipped\":0,\"total_bytes\":1024,\"bytes_restored\":1024,\"errors\":0}",
        });
        ResticEngine.ParseResticRestoreSummary(output,
            out var filesRestored, out var bytesRestored,
            out var totalFiles, out var totalBytes, out var errors);
        Assert.Equal(42, filesRestored);
        Assert.Equal(42, totalFiles);
        Assert.Equal(1024, bytesRestored);
        Assert.Equal(1024, totalBytes);
        Assert.Equal(0, errors);
    }

    [Fact]
    public void ParseResticRestoreSummary_ReportsErrorsCountWhenNonZero()
    {
        var output = "{\"message_type\":\"summary\",\"total_files\":10,\"files_restored\":7,\"errors\":3}";
        ResticEngine.ParseResticRestoreSummary(output,
            out var filesRestored, out var _, out var totalFiles, out var _, out var errors);
        Assert.Equal(7, filesRestored);
        Assert.Equal(10, totalFiles);
        Assert.Equal(3, errors);
    }

    [Fact]
    public void ParseResticRestoreSummary_NoSummary_ReturnsSentinels()
    {
        // No summary line in the captured output -> every out param
        // stays -1 so the caller falls back to its pattern-count
        // approximation.
        ResticEngine.ParseResticRestoreSummary("not-json\n{\"message_type\":\"status\"}",
            out var filesRestored, out var bytesRestored, out var totalFiles,
            out var totalBytes, out var errors);
        Assert.Equal(-1, filesRestored);
        Assert.Equal(-1, bytesRestored);
        Assert.Equal(-1, totalFiles);
        Assert.Equal(-1, totalBytes);
        Assert.Equal(-1, errors);
    }

    [Fact]
    public void PickSnapshotIdForRevision_NewestFirstOrdering()
    {
        // Revision number convention: 1 = oldest, N = newest. The
        // input list is sorted newest-first by the caller, so revision
        // N maps to index 0 and revision 1 maps to the last entry.
        var sorted = new[] { Snapshot("newest", 3000), Snapshot("middle", 2000), Snapshot("oldest", 1000) };

        Assert.Equal("newest", ResticEngine.PickSnapshotIdForRevision(sorted, revision: 3));
        Assert.Equal("middle", ResticEngine.PickSnapshotIdForRevision(sorted, revision: 2));
        Assert.Equal("oldest", ResticEngine.PickSnapshotIdForRevision(sorted, revision: 1));
    }

    [Fact]
    public void PickSnapshotIdForRevision_OutOfRangeThrowsWithChainSize()
    {
        // Revisions are user-supplied: a stale Browser view can ask
        // for a revision that's been pruned. We surface the chain's
        // current size in the message so the caller can render a
        // useful error.
        var sorted = new[] { Snapshot("a", 1000), Snapshot("b", 2000) };

        var lo = Assert.Throws<ArgumentOutOfRangeException>(
            () => ResticEngine.PickSnapshotIdForRevision(sorted, revision: 0));
        Assert.Contains("2 revision", lo.Message);

        var hi = Assert.Throws<ArgumentOutOfRangeException>(
            () => ResticEngine.PickSnapshotIdForRevision(sorted, revision: 3));
        Assert.Contains("2 revision", hi.Message);
    }

    [Fact]
    public void PickSnapshotIdForRevision_EmptyChainAlwaysThrows()
    {
        // No snapshots = no revisions; every call must throw rather
        // than handing back a "" snapshot id that would then go to restic.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ResticEngine.PickSnapshotIdForRevision(System.Array.Empty<ResticEngine.PlanSnapshot>(), revision: 1));
    }

    private static ResticEngine.PlanSnapshot Snapshot(string id, long unixMs) => new(id, unixMs, 0, 0, []);

    [Fact]
    public void BuildBackupFlags_addsLimitUpload_whenBandwidthSet()
    {
        // restic --limit-upload <KiB/s> — surfaces BandwidthLimitKBps.
        // 0 = unlimited; never emit the flag (positive-int required).
        var b = new Backspace.Models.Backup { BandwidthLimitKBps = 1024 };
        var flags = ResticEngine.BuildBackupFlags(b);
        Assert.Contains("--limit-upload", flags);
        Assert.Contains("1024", flags);

        var unlimited = ResticEngine.BuildBackupFlags(
            new Backspace.Models.Backup { BandwidthLimitKBps = 0 });
        Assert.DoesNotContain("--limit-upload", unlimited);
    }

    [Fact]
    public void FindRestoredFile_LocatesByBareCombine()
    {
        // The straightforward layout: caller-relative path placed
        // directly under the target. ResticUriResolver-backed restores
        // sometimes land files this way (when the snapshot has no
        // absolute-path prefix).
        var tmpRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "restic-find-bare-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmpRoot, "sub"));
        var f = System.IO.Path.Combine(tmpRoot, "sub", "hello.txt");
        System.IO.File.WriteAllText(f, "hi");
        try
        {
            var found = ResticEngine.FindRestoredFile(
                targetPath: tmpRoot, sourcePath: "/data/originals", relativePath: "sub/hello.txt");
            Assert.Equal(f, found);
        }
        finally { try { System.IO.Directory.Delete(tmpRoot, recursive: true); } catch { } }
    }

    [Fact]
    public void FindRestoredFile_LocatesUnderPreservedSourceTree()
    {
        // Restic preserves absolute source paths under the target —
        // `/data/originals/sub/hello.txt` lands at
        // `<target>/data/originals/sub/hello.txt`. Resolver must
        // unwrap that layout so the caller's relative-path bookkeeping
        // matches up.
        var tmpRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "restic-find-prefixed-" + System.Guid.NewGuid().ToString("N"));
        var prefixed = System.IO.Path.Combine(tmpRoot, "data", "originals", "sub");
        System.IO.Directory.CreateDirectory(prefixed);
        var f = System.IO.Path.Combine(prefixed, "hello.txt");
        System.IO.File.WriteAllText(f, "hi");
        try
        {
            var found = ResticEngine.FindRestoredFile(
                targetPath: tmpRoot, sourcePath: "/data/originals", relativePath: "sub/hello.txt");
            Assert.Equal(f, found);
        }
        finally { try { System.IO.Directory.Delete(tmpRoot, recursive: true); } catch { } }
    }

    [Fact]
    public void FindRestoredFile_LocatesUnderPreservedUncSourceTree()
    {
        // UNC source paths like \\server\share\foo need their double-
        // backslash anchor stripped (not eaten by a generic leading-
        // separator trim that would leave us reading "share\foo" and
        // re-anchoring at the wrong point). After the strip, the
        // restic-restored layout under target is server\share\sub\hello.
        var tmpRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "restic-find-unc-" + System.Guid.NewGuid().ToString("N"));
        var prefixed = System.IO.Path.Combine(tmpRoot, "server", "share", "sub");
        System.IO.Directory.CreateDirectory(prefixed);
        var f = System.IO.Path.Combine(prefixed, "hello.txt");
        System.IO.File.WriteAllText(f, "hi");
        try
        {
            var found = ResticEngine.FindRestoredFile(
                targetPath: tmpRoot, sourcePath: @"\\server\share", relativePath: "sub/hello.txt");
            Assert.Equal(f, found);
        }
        finally { try { System.IO.Directory.Delete(tmpRoot, recursive: true); } catch { } }
    }

    [Fact]
    public void FindRestoredFile_ReturnsNullWhenAbsent()
    {
        var tmpRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "restic-find-absent-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tmpRoot);
        try
        {
            Assert.Null(ResticEngine.FindRestoredFile(
                targetPath: tmpRoot, sourcePath: "/x", relativePath: "no/such/file.txt"));
        }
        finally { try { System.IO.Directory.Delete(tmpRoot, recursive: true); } catch { } }
    }

    [Fact]
    public async Task PruneToLatestAsync_WithoutRestic_SaysTheComponentIsMissing()
    {
        // "Free up space" runs restic directly (U175): with no restic binary
        // the engine says a component is missing (a FileNotFoundException)
        // before touching the destination.
        var engine = MakeEngineWithoutSidecar();
        await Assert.ThrowsAnyAsync<System.IO.FileNotFoundException>(() =>
            engine.PruneToLatestAsync(new Backup(), "/x", new Destination(), "default", default));
    }

    [Fact]
    public async Task ChangeStoragePasswordAsync_FailureSurfacesHelpfulError()
    {
        // Two environments: CI (no bundled binary, AppPaths.ResticBinary
        // doesn't resolve → install hint) and dev (binary present, runs
        // restic key passwd against a bogus repo → restic's own
        // "Fatal: repository does not exist" line). Either way the
        // contract is: non-zero exit + a non-empty message a user can
        // act on. Asserting the literal word "restic" would only pass
        // in CI, so we check the broader "helpful failure" contract.
        var engine = MakeEngineWithoutSidecar();
        var dest = new Destination { Id = "d", Kind = DestinationKind.LocalFolder, PathOrSubpath = "/tmp" };
        var (exit, output) = await engine.ChangeStoragePasswordAsync(dest, "default", "old", "new", default);
        Assert.NotEqual(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(output), $"Expected a non-empty error message; got: '{output}'");
        var helpful =
            output.Contains("restic", System.StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Fatal", System.StringComparison.Ordinal) ||
            output.Contains("repository", System.StringComparison.OrdinalIgnoreCase);
        Assert.True(helpful, $"Expected error to mention restic / Fatal / repository; got: '{output}'");
    }

    [Fact]
    public void BuildResticPasswordChangeEnv_CarriesRepositoryAndPasswords()
    {
        // The env block restic key passwd consumes needs the URI on
        // RESTIC_REPOSITORY, the current password on RESTIC_PASSWORD,
        // and any URI-specific cloud credentials the resolver
        // emitted. Pin the wire shape so a typo in a key (which
        // restic would silently ignore) surfaces as a failed test.
        var resticEnv = new ResticEnvironment(
            Uri: "s3:s3.us-east-1.amazonaws.com/mybucket/path",
            Env: new[] { "AWS_ACCESS_KEY_ID=AKIAX", "AWS_SECRET_ACCESS_KEY=secret" });

        var env = ResticEngine.BuildResticPasswordChangeEnv(resticEnv, oldPassword: "hunter2");

        Assert.Equal("s3:s3.us-east-1.amazonaws.com/mybucket/path", env["RESTIC_REPOSITORY"]);
        Assert.Equal("hunter2",                                     env["RESTIC_PASSWORD"]);
        Assert.Equal("AKIAX",                                       env["AWS_ACCESS_KEY_ID"]);
        Assert.Equal("secret",                                      env["AWS_SECRET_ACCESS_KEY"]);
    }

    [Fact]
    public void BuildResticPasswordChangeEnv_HandlesValuesWithEqualsSigns()
    {
        // OAuth tokens / base64 secrets contain '=' as padding. The
        // env-line parser must split on the FIRST '=' only, not
        // every occurrence -- the latter would truncate the value.
        var resticEnv = new ResticEnvironment(
            Uri: "rclone:dropbox:store",
            Env: new[] { "RCLONE_TOKEN={\"refresh_token\":\"abc==\"}" });

        var env = ResticEngine.BuildResticPasswordChangeEnv(resticEnv, "p");

        Assert.Equal("{\"refresh_token\":\"abc==\"}", env["RCLONE_TOKEN"]);
    }

    [Fact]
    public void RaiseLineWritten_NotifiesSubscribers()
    {
        var engine = MakeEngineWithoutSidecar();
        string? captured = null;
        engine.LineWritten += line => captured = line;

        engine.RaiseLineWritten("hello");

        Assert.Equal("hello", captured);
    }

    [Fact]
    public async Task BackupPruneCheckAsync_WithoutRestic_SaysTheComponentIsMissing()
    {
        // A missing restic surfaces as a FileNotFoundException (a component
        // is missing) before anything else is tried.
        var engine = MakeEngineWithoutSidecar();
        await Assert.ThrowsAnyAsync<System.IO.FileNotFoundException>(() =>
            engine.BackupPruneCheckAsync(new Backup(), "/x", new Destination(), "default", default));
    }

    /// <summary>A restic id (a snapshot's, a repository's) is exactly 64 hex
    /// characters; anything else is never taken for one.</summary>
    [Theory]
    [InlineData("",                                                                   false)]
    [InlineData("bf683127-2b82-b9e3-86c6-419f9615a2c5",                               false)] // UUID shape
    [InlineData("a3b1c2d4e5f60123456789abcdef0123456789abcdef0123456789abcdef0123",   true)]
    [InlineData("a3b1c2d4e5f60123456789abcdef0123456789abcdef0123456789abcdef012",    false)] // 63
    [InlineData("a3b1c2d4e5f60123456789abcdef0123456789abcdef0123456789abcdef01234",  false)] // 65
    [InlineData("z3b1c2d4e5f60123456789abcdef0123456789abcdef0123456789abcdef0123",   false)] // non-hex
    public void IsValidSnapshotId_acceptsOnly64CharLowerOrMixedHex(string guid, bool expected)
    {
        Assert.Equal(expected, ResticEngine.IsValidSnapshotId(guid));
    }

    /// <summary>
    /// Regression for the silent-success class bug: an unknown or empty
    /// result word was once a success. A run that never said it succeeded
    /// is NOT a success.
    /// </summary>
    [Theory]
    [InlineData("STATUS_SUCCESS",        Backspace.Models.BackupRunStatus.Success)]
    [InlineData("STATUS_WARNING",        Backspace.Models.BackupRunStatus.Warning)]
    [InlineData("STATUS_ERROR",          Backspace.Models.BackupRunStatus.Failed)]
    [InlineData("STATUS_USER_CANCELLED", Backspace.Models.BackupRunStatus.Skipped)]
    [InlineData("",                      Backspace.Models.BackupRunStatus.Failed)]
    [InlineData("STATUS_UNKNOWN",        Backspace.Models.BackupRunStatus.Failed)]
    [InlineData("something_weird",       Backspace.Models.BackupRunStatus.Failed)]
    public void StatusFromTerminal_unknownOrEmpty_defaultsToFailed(string input, Backspace.Models.BackupRunStatus expected)
    {
        Assert.Equal(expected, ResticEngine.StatusFromTerminal(input));
    }

    // ---- helpers ----

    private static ResticEngine MakeEngineWithoutSidecar()
        => new(new SecretsStore(), resticBinary: "/nonexistent/restic");
}
