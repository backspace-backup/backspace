using System;
using System.Linq;
using System.Text.Json;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U112 (3 November 2026): a backup showed no progress while it
/// uploaded, because only the operation's state was read. The engine's own
/// progress (restic's status, recorded by Backrest) now becomes a line.</summary>
public sealed class BackupLiveProgressTests
{
    // Backrest's JSON for a running backup: 64-bit numbers arrive as strings.
    private static JsonElement Operation(double fraction, long bytesDone, long totalBytes, long filesDone, long totalFiles)
        => JsonSerializer.SerializeToElement(new
        {
            lastStatus = new
            {
                status = new
                {
                    percentDone = fraction, totalFiles = totalFiles.ToString(), totalBytes = totalBytes.ToString(),
                    filesDone = filesDone.ToString(), bytesDone = bytesDone.ToString(), currentFile = new[] { @"C:\Users\me\a.txt" },
                },
            },
        });

    [Fact]
    public void A_running_backup_reports_percent_bytes_files_and_speed()
    {
        var live = new BackupLiveProgress();
        var start = new DateTime(2026, 11, 3, 7, 30, 0, DateTimeKind.Utc);
        Assert.Equal("[progress] 10.0% · Backing up · 5.0 GB of 50 GB · 1,000 of 10,000 files",
            live.Next(Operation(0.1, 5_000_000_000, 50_000_000_000, 1_000, 10_000), start));
        // Too soon for another line.
        Assert.Null(live.Next(Operation(0.11, 5_500_000_000, 50_000_000_000, 1_100, 10_000), start.AddSeconds(2)));
        // Five seconds later: 150 MB more, so 30 MB/s.
        Assert.Equal("[progress] 10.3% · Backing up · 5.2 GB of 50 GB · 1,200 of 10,000 files · 30 MB/s",
            live.Next(Operation(0.103, 5_150_000_000, 50_000_000_000, 1_200, 10_000), start.AddSeconds(5)));
    }

    [Fact]
    public void Before_anything_is_done_it_says_scanning_and_unchanged_progress_is_not_repeated()
    {
        var live = new BackupLiveProgress();
        var start = DateTime.UtcNow;
        Assert.Equal("[progress] 0.0% · Scanning · 0 B of 12 GB · 0 of 18,000 files",
            live.Next(Operation(0, 0, 12_000_000_000, 0, 18_000), start));
        Assert.Null(live.Next(Operation(0, 0, 12_000_000_000, 0, 18_000), start.AddMinutes(1)));
        Assert.Null(live.Next(null, start.AddMinutes(2)));
        Assert.Null(live.Next(JsonDocument.Parse("{}").RootElement.Clone(), start.AddMinutes(3)));
    }

    [Fact]
    public void The_backup_card_receives_the_percentage_and_the_details()
    {
        var progress = new BackupProgressService();
        progress.RegisterRunCells("b1", ["C:\\"], ["Google Drive"]);
        progress.ReportCellStarted("b1", "C:\\", "Google Drive");
        progress.ReportLine("b1", "C:\\", "Google Drive", "[progress] 42.5% · Backing up · 21 GB of 50 GB · 4,200 of 10,000 files · 31 MB/s");
        var snapshot = progress.Snapshot("b1")!;
        var source = snapshot.Sources["C:\\"];
        Assert.Equal(42.5, source.PercentComplete, 1);
        Assert.Equal("Backing up · 21 GB of 50 GB · 4,200 of 10,000 files · 31 MB/s", source.Detail);
    }

    [Fact]
    public void A_finished_backup_ends_with_a_recap_and_a_failed_one_has_none()
    {
        var finished = JsonSerializer.SerializeToElement(new
        {
            lastStatus = new
            {
                summary = new
                {
                    filesNew = "891", filesChanged = "12236", filesUnmodified = "704803", dataAdded = "387529000",
                    totalFilesProcessed = "717930", totalBytesProcessed = "194637000000", totalDuration = 844.2, snapshotId = "abc",
                },
            },
        });
        Assert.Equal("[recap] Backup complete: 717,930 files (195 GB) checked · 891 new, 12,236 changed, 704,803 unchanged · 388 MB added to the storage · took 14m 04s",
            BackupLiveProgress.Recap(finished));
        Assert.Null(BackupLiveProgress.Recap(Operation(0.5, 1, 2, 1, 2)));
        Assert.Null(BackupLiveProgress.Recap(null));
    }

    // ---- U115 (4 November 2026): what Duplicacy's logs showed ----

    [Fact]
    public void Time_left_appears_once_data_has_moved_for_a_while()
    {
        var live = new BackupLiveProgress();
        var start = new DateTime(2026, 11, 4, 7, 0, 0, DateTimeKind.Utc);
        Assert.DoesNotContain("left", live.Next(Operation(0.1, 5_000_000_000, 50_000_000_000, 1_000, 10_000), start));
        // 20 s later, 600 MB more: 30 MB/s on average, 44.4 GB to go, about 25 min.
        var line = live.Next(Operation(0.112, 5_600_000_000, 50_000_000_000, 1_100, 10_000), start.AddSeconds(20))!;
        Assert.EndsWith(" · about 25 min left", line);
        Assert.Equal("under a minute left", BackupLiveProgress.TimeLeft(TimeSpan.FromSeconds(40)));
        Assert.Equal("about 2 h 05 min left", BackupLiveProgress.TimeLeft(TimeSpan.FromMinutes(125)));
    }

    [Fact]
    public void While_nothing_has_moved_no_time_left_is_guessed()
    {
        var live = new BackupLiveProgress();
        var start = DateTime.UtcNow;
        live.Next(Operation(0, 0, 12_000_000_000, 0, 18_000), start);
        Assert.DoesNotContain("left", live.Next(Operation(0, 0, 13_000_000_000, 0, 19_000), start.AddMinutes(5)));
    }

    [Fact]
    public void The_recap_names_the_snapshot_it_made()
    {
        var finished = JsonSerializer.SerializeToElement(new
        {
            lastStatus = new
            {
                summary = new
                {
                    filesNew = "1", filesChanged = "2", filesUnmodified = "3", dataAdded = "1000",
                    totalFilesProcessed = "6", totalBytesProcessed = "5000", totalDuration = 61.0,
                    snapshotId = "1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f809",
                },
            },
        });
        Assert.EndsWith(" · took 1m 01s · snapshot 1a2b3c4d", BackupLiveProgress.Recap(finished));
    }

    [Fact]
    public void Each_changed_file_becomes_a_line_but_folders_and_totals_do_not()
    {
        Assert.Equal("[file] added: C:\\Users\\me\\new.txt", BackupLiveProgress.FileLine("+    /C/Users/me/new.txt"));
        Assert.Equal("[file] changed: C:\\Users\\me\\report.docx", BackupLiveProgress.FileLine("M    /C/Users/me/report.docx"));
        Assert.Equal("[file] removed: C:\\Users\\me\\old.txt", BackupLiveProgress.FileLine("-    /C/Users/me/old.txt"));
        Assert.Equal("[file] details changed: C:\\Users\\me\\a b.txt", BackupLiveProgress.FileLine("U    /C/Users/me/a b.txt"));
        Assert.Null(BackupLiveProgress.FileLine("+    /C/Users/me/new folder/"));
        Assert.Null(BackupLiveProgress.FileLine("comparing snapshot 1a2b3c4d to 5e6f7081:"));
        Assert.Null(BackupLiveProgress.FileLine("Files:           2 new,     1 removed,     3 changed"));
        Assert.Null(BackupLiveProgress.FileLine("  Added:   12.000 MiB"));
        Assert.Null(BackupLiveProgress.FileLine(""));
    }

    [Fact]
    public void A_first_backup_lists_every_file_from_the_snapshot_listing()
    {
        Assert.Equal("[file] added: C:\\Users\\me\\Documents\\taxes 2026.pdf", BackupLiveProgress.ListedFileLine(
            "{\"name\":\"taxes 2026.pdf\",\"type\":\"file\",\"path\":\"/C/Users/me/Documents/taxes 2026.pdf\",\"size\":1024,\"struct_type\":\"node\"}"));
        Assert.Null(BackupLiveProgress.ListedFileLine("{\"name\":\"Documents\",\"type\":\"dir\",\"path\":\"/C/Users/me/Documents\",\"struct_type\":\"node\"}"));
        Assert.Null(BackupLiveProgress.ListedFileLine("{\"time\":\"2026-11-04T07:00:00Z\",\"paths\":[\"C:\\\\Users\\\\me\"],\"struct_type\":\"snapshot\"}"));
        Assert.Null(BackupLiveProgress.ListedFileLine("not json"));
        // A long path is kept whole, never shortened.
        var deep = "/C/" + string.Join("/", Enumerable.Repeat("a fairly long folder name", 20)) + "/file.txt";
        Assert.EndsWith("a fairly long folder name\\file.txt", BackupLiveProgress.FileLine("M    " + deep));
        Assert.DoesNotContain("…", BackupLiveProgress.FileLine("M    " + deep));
    }

    [Fact]
    public void The_file_list_runs_restic_directly_with_the_repository_settings()
    {
        var repo = new BackrestRepo { Uri = "rclone:gd:backups", Password = "fixture-password", Flags = { "-o", "rclone.program=rclone.exe" },
            Env = { "RCLONE_CONFIG=C:\\cfg\\rclone.conf" } };
        var start = ResticBackrestEngine.BuildResticStartInfo("C:/engines/restic.exe", repo, "diff", "--no-lock", "aaaa", "bbbb");
        Assert.Equal(new[] { "-r", "rclone:gd:backups", "-o", "rclone.program=rclone.exe", "diff", "--no-lock", "aaaa", "bbbb" }, start.ArgumentList);
        Assert.Equal("fixture-password", start.Environment["RESTIC_PASSWORD"]);
        Assert.Equal("C:\\cfg\\rclone.conf", start.Environment["RCLONE_CONFIG"]);
    }

    [Fact]
    public void The_opening_lines_say_what_the_backup_starts_from()
    {
        var backup = new Backspace.Models.Backup { FiltersText = "-*.tmp\n-*.log", UseVss = true };
        var destination = new Backspace.Models.Destination { Name = "Google Drive" };
        var first = ResticBackrestEngine.SetupLines(backup, @"C:\Users\me", destination, null).ToList();
        Assert.Equal("[setup] No earlier backup of this folder in «Google Drive»: everything is read and sent.", first[0]);
        // The two rules here plus the essential exclusions, which always apply.
        Assert.Matches(@"^\[setup\] \d+ exclusion rules loaded", first[1]);
        Assert.True(int.Parse(first[1].Split(' ')[1]) > 2);
        Assert.Equal(OperatingSystem.IsWindows()
            ? "[setup] Shadow copy on: open and locked files are read from a snapshot of the drive."
            : "[setup] Shadow copy off: files another program keeps locked may be skipped.", first[2]);
        var when = new DateTime(2026, 11, 3, 18, 40, 0, DateTimeKind.Utc);
        var next = ResticBackrestEngine.SetupLines(backup, @"C:\Users\me", destination, ("abcdef0123456789", when)).First();
        Assert.Equal($"[setup] Previous backup to «Google Drive»: {when.ToLocalTime():yyyy-MM-dd HH:mm}, snapshot abcdef01. Only changes are sent.", next);
    }
}
