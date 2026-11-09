using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Online-only files (U168, 9 November 2026). A real placeholder needs a
/// cloud provider's filter driver, so these tests use the attribute values
/// providers set, and a local file marked offline with SetFileAttributes,
/// which is exactly what the detection reads. The same table is in restic
/// patch 0003's tests (engine/restic/patches).
/// </summary>
public class CloudFileBehaviorTests
{
    private const FileAttributes Pinned = (FileAttributes)0x00080000;

    public static IEnumerable<object[]> Attributes() => new[]
    {
        // Dropbox online-only as an ordinary process sees it (measured on the
        // owner's PC): ARCHIVE | SPARSE_FILE | REPARSE_POINT | OFFLINE.
        new object[] { (FileAttributes)0x1620, true },
        new object[] { FileAttributes.Offline, true },
        new object[] { CloudFileBehavior.RecallOnOpen, true },
        new object[] { FileAttributes.Archive | FileAttributes.ReparsePoint | CloudFileBehavior.RecallOnDataAccess, true },
        new object[] { FileAttributes.Directory | CloudFileBehavior.RecallOnDataAccess, true },
        // Not online-only: the data is on this computer.
        new object[] { FileAttributes.Archive, false },
        new object[] { FileAttributes.Archive | FileAttributes.SparseFile, false },
        new object[] { FileAttributes.Archive | FileAttributes.ReparsePoint, false }, // downloaded placeholder, link
        new object[] { FileAttributes.Archive | Pinned, false },
        new object[] { FileAttributes.Directory | FileAttributes.Offline, false },
        new object[] { FileAttributes.Directory | CloudFileBehavior.RecallOnOpen, false },
    };

    [Theory]
    [MemberData(nameof(Attributes))]
    public void Online_only_is_decided_by_the_same_rule_as_the_engine(FileAttributes attributes, bool cloud)
    {
        Assert.Equal(cloud, CloudFileBehavior.IsCloudOnly(attributes));
        Assert.Equal(cloud, TreeWalk.IsCloudOnly(attributes));
    }

    [Fact]
    public void A_file_marked_offline_is_online_only_and_its_content_is_left_alone()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("cloud-offline");
        var file = Path.Combine(workspace.Root, "online-only.txt");
        File.WriteAllText(file, "content");
        File.SetAttributes(file, FileAttributes.Offline);
        try
        {
            Assert.True(CloudFileBehavior.IsCloudOnly(file));
            // Only the attributes were read.
            Assert.Equal(FileAttributes.Offline, File.GetAttributes(file));
        }
        finally { File.SetAttributes(file, FileAttributes.Normal); }
    }

    [Fact]
    public void IsCloudOnly_OnRegularFile_ReturnsFalse()
    {
        using var workspace = new TempWorkspace("cloud-regular");
        var tmp = Path.Combine(workspace.Root, "local.txt");
        File.WriteAllText(tmp, "ordinary local content");
        Assert.False(CloudFileBehavior.IsCloudOnly(tmp));
        Assert.False(CloudFileBehavior.IsCloudOnly(workspace.Root));
    }

    [Fact]
    public void IsCloudOnly_OnMissingFile_ReturnsFalse()
    {
        // Caller may walk a directory while the user moves files in
        // and out — a transient FileNotFoundException would surface as
        // a stuck enumeration. Predicate swallows the error.
        var nonexistent = Path.Combine(Path.GetTempPath(), "no-such-file-" + Guid.NewGuid().ToString("N"));
        Assert.False(CloudFileBehavior.IsCloudOnly(nonexistent));
    }

    [Fact]
    public void IsCloudOnly_OnEmptyOrNullPath_ReturnsFalse()
    {
        Assert.False(CloudFileBehavior.IsCloudOnly(""));
        Assert.False(CloudFileBehavior.IsCloudOnly((string)null!));
    }

    [Fact]
    public void ExplainBackupBehavior_DistinctMessagesPerFlag()
    {
        var on  = CloudFileBehavior.ExplainBackupBehavior(excludeCloudFiles: true);
        var off = CloudFileBehavior.ExplainBackupBehavior(excludeCloudFiles: false);

        Assert.NotEqual(on, off);
        Assert.Contains("skipped", on, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("download", off, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_skipped_phrase_counts_files()
    {
        Assert.Null(CloudFileBehavior.SkippedPhrase(0));
        Assert.Equal("1 online-only file skipped", CloudFileBehavior.SkippedPhrase(1));
        Assert.Equal(8_335.ToString("N0") + " online-only files skipped", CloudFileBehavior.SkippedPhrase(8_335));
    }

    /// <summary>A source with two local files and two online-only ones, one
    /// of them in a folder the rules exclude.</summary>
    private static string Source(TempWorkspace workspace, out string[] offline)
    {
        var root = workspace.Sub("source");
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        File.WriteAllBytes(Path.Combine(root, "here.docx"), new byte[300]);
        File.WriteAllBytes(Path.Combine(root, "there.docx"), new byte[200]);
        offline = new[] { Path.Combine(root, "online-only.mp4"), Path.Combine(root, "cache", "online-only.bin") };
        foreach (var f in offline)
        {
            File.WriteAllBytes(f, new byte[9_000]);
            File.SetAttributes(f, FileAttributes.Offline);
        }
        return root;
    }

    private static void Restore(string[] offline)
    {
        foreach (var f in offline) File.SetAttributes(f, FileAttributes.Normal);
    }

    [Fact]
    public void A_run_counts_the_online_only_files_its_backup_skips()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("cloud-run");
        var root = Source(workspace, out var offline);
        try
        {
            var rules = TreeWalk.Root(root).Replace('\\', '/') + "/cache/";
            var skipping = ChangePlan.List(root, new SourceSizeRules(rules, true), TimeSpan.FromMinutes(1), CancellationToken.None, out var skipped);
            Assert.Equal(1, skipped); // the excluded folder's file is not counted
            Assert.Equal(new[] { "here.docx", "there.docx" }, skipping!.Select(f => f.Path).OrderBy(p => p));

            var reading = ChangePlan.List(root, new SourceSizeRules(rules, false), TimeSpan.FromMinutes(1), CancellationToken.None, out var none);
            Assert.Equal(0, none);
            Assert.Contains(reading!, f => f.Path == "online-only.mp4");
        }
        finally { Restore(offline); }
    }

    [Fact]
    public async Task Simulate_counts_online_only_files_instead_of_listing_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("cloud-simulate");
        var root = Source(workspace, out var offline);
        try
        {
            var backup = new Backup { SourcePaths = { root }, FiltersText = "", ExcludeCloudFiles = true };
            var entries = new List<SimulatedEntry>();
            var result = await new FilterSimulator().SimulateAsync(backup, entries.Add, CancellationToken.None);
            Assert.Equal(2, result.CloudOnly);
            Assert.DoesNotContain(entries, e => e.Path.Contains("online-only", StringComparison.Ordinal));
            Assert.Equal(2, entries.Count(e => e.Included));

            backup.ExcludeCloudFiles = false;
            entries.Clear();
            result = await new FilterSimulator().SimulateAsync(backup, entries.Add, CancellationToken.None);
            Assert.Equal(2, result.CloudOnly);
            Assert.Equal(4, entries.Count(e => e.Included)); // read, as before
        }
        finally { Restore(offline); }
    }

    [Fact]
    public void Simulate_says_how_many_online_only_files_are_skipped()
    {
        Assert.Null(BackupEditorViewModel.SimulationCloudNote(0, skipped: true));
        Assert.StartsWith(8_335.ToString("N0") + " online-only files skipped:", BackupEditorViewModel.SimulationCloudNote(8_335, skipped: true));
        Assert.Contains("is off", BackupEditorViewModel.SimulationCloudNote(2, skipped: false));
    }

    [Fact]
    public void The_run_summary_and_the_card_hover_say_how_many_were_skipped()
    {
        var started = new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc);
        var run = new RunRecord
        {
            StartedUtc = started, EndedUtc = started.AddSeconds(90), Status = BackupRunStatus.Success,
            BytesUploaded = 1_000_000, FilesNew = 3, CloudOnlySkipped = 8_335,
        };
        var summary = BackupOrchestrator.BuildSummary(run);
        var phrase = 8_335.ToString("N0") + " online-only files skipped";
        Assert.EndsWith(" · " + phrase, summary);
        Assert.DoesNotContain("warning", summary);

        var (label, hover) = LastRunLine.For(summary, BackupRunStatus.Success, started.AddSeconds(90), started.AddMinutes(5));
        Assert.Contains(phrase + ", kept only in the cloud", hover);
        Assert.Contains("took 1m 30s", hover);
        Assert.DoesNotContain("online-only", label);

        // Nothing skipped: nothing said.
        run.CloudOnlySkipped = 0;
        Assert.DoesNotContain("online-only", BackupOrchestrator.BuildSummary(run));
        Assert.DoesNotContain("online-only", LastRunLine.For("1 MB uploaded · in 4s", BackupRunStatus.Success, started, started).Hover);
        // A run that did nothing else still reads "Completed in".
        var noop = new RunRecord { StartedUtc = started, EndedUtc = started.AddSeconds(6), Status = BackupRunStatus.Success, CloudOnlySkipped = 1 };
        Assert.Equal("Completed in 6s · 1 online-only file skipped", BackupOrchestrator.BuildSummary(noop));
        Assert.Contains("1 online-only file skipped, kept only in the cloud", LastRunLine.For(BackupOrchestrator.BuildSummary(noop), BackupRunStatus.Success, started, started).Hover);
    }
}
