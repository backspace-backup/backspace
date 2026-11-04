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
    public void Once_the_count_is_done_a_backup_reports_percent_bytes_files_and_time_left()
    {
        var live = new BackupLiveProgress();
        var start = new DateTime(2026, 11, 3, 7, 30, 0, DateTimeKind.Utc);
        // Totals just found: restic may still be counting, so no percentage yet.
        Assert.Equal("[progress] 0.0% · Backing up · 4.7 GB · 1,000 / 10,000+ files",
            live.Next(Operation(0.1, 5_000_000_000, 50_000_000_000, 1_000, 10_000), start));
        // Too soon for another line.
        Assert.Null(live.Next(Operation(0.11, 5_500_000_000, 50_000_000_000, 1_100, 10_000), start.AddSeconds(2)));
        // The totals stayed the same for 30 s: they are final. 150 MB in 30 s,
        // 44.85 GB to go: about 2 h 29 min.
        Assert.Equal("[progress] 10.3% · Backing up · 4.8 / 47 GB · 1,200 / 10,000 files · about 2 h 29 min left",
            live.Next(Operation(0.103, 5_150_000_000, 50_000_000_000, 1_200, 10_000), start.AddSeconds(30)));
    }

    [Fact]
    public void Before_anything_is_done_it_says_scanning_and_unchanged_progress_is_not_repeated()
    {
        var live = new BackupLiveProgress();
        var start = DateTime.UtcNow;
        Assert.Equal("[progress] 0.0% · Scanning · 0 B · 0 / 18,000+ files",
            live.Next(Operation(0, 0, 12_000_000_000, 0, 18_000), start));
        Assert.Null(live.Next(Operation(0, 0, 12_000_000_000, 0, 18_000), start.AddMinutes(1)));
        Assert.Null(live.Next(null, start.AddMinutes(2)));
        Assert.Null(live.Next(JsonDocument.Parse("{}").RootElement.Clone(), start.AddMinutes(3)));
    }

    // ---- U124 (4 November 2026): the card showed 91% and "under a minute
    // left" on a first backup of C:\ while restic was still counting the
    // drive, at "622 MB/s" to Google Drive. These are the numbers it logged. ----

    [Fact]
    public void While_restic_is_still_counting_the_percentage_is_never_of_what_was_found_so_far()
    {
        var live = new BackupLiveProgress();
        var start = new DateTime(2026, 11, 4, 8, 9, 50, DateTimeKind.Utc);
        // Restic said 90.7% of the first 97 MB it had found.
        var first = live.Next(Operation(0.907, 88_000_000, 97_000_000, 614, 683), start)!;
        Assert.StartsWith("[progress] 0.0% · Backing up · 84 MB · 614 / 683+ files", first);
        // Totals growing at every poll: still no percentage and no time left.
        var t = start;
        foreach (var (pct, done, total, files, found) in new[] { (0.678, 19_000_000_000L, 28_000_000_000L, 37_113L, 62_564L),
                     (0.853, 25_000_000_000, 29_000_000_000, 40_432, 68_493), (0.407, 36_000_000_000, 89_000_000_000, 140_495, 264_466) })
        {
            t = t.AddSeconds(5);
            var line = live.Next(Operation(pct, done, total, files, found), t)!;
            Assert.StartsWith("[progress] 0.0% · Backing up · ", line);
            Assert.EndsWith("+ files", line);
            Assert.DoesNotContain("left", line);
            Assert.DoesNotContain("/s", line); // no speed made of files that needed nothing sent
        }
    }

    [Fact]
    public void While_counting_the_percentage_is_of_the_size_expected_when_one_is_known()
    {
        // The last backup read 150 GB (or the estimate says so).
        var live = new BackupLiveProgress(expectedBytes: 150_000_000_000);
        var line = live.Next(Operation(0.678, 19_000_000_000, 28_000_000_000, 37_113, 62_564), DateTime.UtcNow)!;
        Assert.Equal("[progress] 12.7% · Backing up · 18 / ~140 GB · 37,113 / 62,564+ files", line);
        // Found more than expected: the share of what was found, never more.
        var bigger = new BackupLiveProgress(expectedBytes: 20_000_000_000);
        Assert.StartsWith("[progress] 67.9% · Backing up · 18 / ~26 GB", bigger.Next(Operation(0.678, 19_000_000_000, 28_000_000_000, 37_113, 62_564), DateTime.UtcNow));
    }

    [Fact]
    public void The_size_expected_can_arrive_while_the_backup_runs()
    {
        // U129: the background service counts the source itself at the start.
        long expected = 0;
        var live = new BackupLiveProgress(() => expected, null);
        var start = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.StartsWith("[progress] 0.0% · Backing up · 18 GB · 37,113 / 62,564+ files", live.Next(Operation(0.9, 19_000_000_000, 21_000_000_000, 37_113, 62_564), start));
        expected = 190_800_000_000; // 177.7 GB, as the card shows
        Assert.StartsWith("[progress] 47.3% · Backing up · 84 / ~178 GB",
            live.Next(Operation(0.95, 90_200_000_000, 95_000_000_000, 269_809, 390_263), start.AddSeconds(5)));
    }

    [Fact]
    public void The_speed_is_of_the_data_sent_to_the_storage()
    {
        long sent = 0;
        var live = new BackupLiveProgress(sentBytes: () => sent);
        var start = new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        Assert.EndsWith(" · 0 B sent", live.Next(Operation(0.1, 5_000_000_000, 50_000_000_000, 1_000, 10_000), start));
        // 3 GB read in 5 s, mostly unchanged files; 60 million bytes actually sent: 11 MB/s.
        sent = 60_000_000;
        Assert.EndsWith(" · 57 MB sent at 11 MB/s", live.Next(Operation(0.16, 8_000_000_000, 50_000_000_000, 4_000, 10_000), start.AddSeconds(5)));
        // When Windows can't tell (two backups at once), no speed rather than a wrong one.
        var unknown = new BackupLiveProgress(sentBytes: () => null);
        Assert.DoesNotContain("sent", unknown.Next(Operation(0.1, 5_000_000_000, 50_000_000_000, 1_000, 10_000), start));
    }

    /// <summary>U133 (4 November 2026): the line ran past the card
    /// ("Backing up · 30 GB of about 178 GB · 88,211 files done, 109,320 found
    /// so far (still counting) · already stored, nothing new to send (880 KB s…"),
    /// and its speed jumped between bytes and megabytes per second.</summary>
    [Fact]
    public void The_line_is_compact_and_its_speed_steady()
    {
        long sent = 900_000;
        var live = new BackupLiveProgress(() => 190_800_000_000, () => sent);
        var start = new DateTime(2026, 11, 4, 21, 20, 0, DateTimeKind.Utc);
        Assert.Equal("[progress] 16.9% · Backing up · 30 / ~178 GB · 88,211 / 109,320+ files · 879 KB sent",
            live.Next(Operation(0.25, 32_200_000_000, 120_000_000_000, 88_211, 109_320), start));
        // Files already stored: a few KB of listings in 30 s is no upload, so no speed.
        sent += 30_000;
        Assert.Equal("[progress] 17.5% · Backing up · 31 / ~178 GB · 90,000 / 111,000+ files · 908 KB sent",
            live.Next(Operation(0.26, 33_400_000_000, 121_000_000_000, 90_000, 111_000), start.AddSeconds(30)));
        // New data: 150 MB in the next 30 s, and a quiet 5 s right after keeps the speed.
        sent += 150_000_000;
        Assert.EndsWith(" · 144 MB sent at 4.8 MB/s", live.Next(Operation(0.27, 33_600_000_000, 122_000_000_000, 90_100, 112_000), start.AddSeconds(60)));
        Assert.EndsWith(" sent at 4.1 MB/s", live.Next(Operation(0.27, 33_610_000_000, 122_000_000_000, 90_110, 112_000), start.AddSeconds(65)));
        // Once restic has counted everything: its own totals, no "~" and no "+".
        Assert.StartsWith("[progress] 53.3% · Backing up · 94 / 177 GB · 385,715 / 586,925 files",
            BackupLiveProgress.Format(new(53.3, 190_100_000_000, 101_000_000_000, 586_925, 385_715), false, 190_800_000_000, null, null));
        Assert.Equal("880 MB / ~178 GB", BackupLiveProgress.Of(922_746_880, 190_800_000_000, estimated: true));
    }

    [Fact]
    public void The_size_a_finished_backup_read_is_taken_from_its_recap()
    {
        Assert.Equal(195L << 30, BackupLiveProgress.CheckedBytes(
            "[recap] Backup complete: 717,930 files (195 GB) checked · 891 new, 12,236 changed, 704,803 unchanged · 388 MB added to the storage · took 14m 04s"));
        Assert.Equal((long)(5.5 * (1L << 30)), Checked("5.5 GB"));
        Assert.Equal(812, Checked("812 B"));
        Assert.Null(BackupLiveProgress.CheckedBytes("[progress] 10.0% · Backing up · 5.0 GB of 50 GB"));
        Assert.Null(BackupLiveProgress.CheckedBytes("Backup complete: 1 files (5 GB) checked"));
        // The card's size then comes from the backup, not from the estimate.
        Assert.Equal(195L << 30, BackupProgressService.TryParseSourceSizeBytes(
            "[recap] Backup complete: 717,930 files (195 GB) checked · 1 new, 0 changed, 0 unchanged · 1 B added to the storage · took 0m 01s"));
    }

    private static long? Checked(string size)
        => BackupLiveProgress.CheckedBytes($"[recap] Backup complete: 6 files ({size}) checked · took 0m 01s");

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
        Assert.Equal("[recap] Backup complete: 717,930 files (181 GB) checked · 891 new, 12,236 changed, 704,803 unchanged · 370 MB added to the storage · took 14m 04s",
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
        // 40 s later, totals unchanged, 1.2 GB more: 30 MB/s on average, 43.8 GB to go, about 25 min.
        var line = live.Next(Operation(0.124, 6_200_000_000, 50_000_000_000, 1_100, 10_000), start.AddSeconds(40))!;
        Assert.EndsWith(" · about 25 min left", line);
        Assert.Equal("under a minute left", BackupLiveProgress.TimeLeft(TimeSpan.FromSeconds(40)));
        Assert.Equal("about 2 h 05 min left", BackupLiveProgress.TimeLeft(TimeSpan.FromMinutes(125)));
    }

    [Fact]
    public void Time_left_follows_the_recent_pace_not_the_average_since_the_start()
    {
        // local_c, 4 November 2026: the first 88 GB were already stored and
        // went by at hundreds of MB/s; then new data went at about 4.5 MB/s.
        // The average since the start said "about 23 min left".
        var live = new BackupLiveProgress();
        var start = new DateTime(2026, 11, 4, 12, 40, 0, DateTimeKind.Utc);
        const long total = 190_000_000_000;
        string? line = null;
        for (var t = 0; t <= 660; t += 30)
        {
            long done = t <= 300 ? 1_000_000_000 + (long)(t / 300.0 * 87_000_000_000) : 88_000_000_000 + (long)((t - 300) * 4_500_000.0);
            line = live.Next(Operation(done / (double)total, done, total, t, 586_638), start.AddSeconds(t)) ?? line;
        }
        // 100.4 GB to go at 4.5 MB/s: about 6 hours, not minutes.
        Assert.Contains(" · about 6 h ", line);
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
        // A folder, not a drive: no drive-top rules (U106).
        Assert.StartsWith("[setup] 3 exclusion rules loaded", first[1]); // its two, and Windows' app aliases (always left out)
        Assert.Equal(OperatingSystem.IsWindows()
            ? "[setup] Shadow copy on: open and locked files are read from a snapshot of the drive."
            : "[setup] Shadow copy off: files another program keeps locked may be skipped.", first[2]);
        var when = new DateTime(2026, 11, 3, 18, 40, 0, DateTimeKind.Utc);
        var next = ResticBackrestEngine.SetupLines(backup, @"C:\Users\me", destination, ("abcdef0123456789", when)).First();
        Assert.Equal($"[setup] Previous backup to «Google Drive»: {when.ToLocalTime():yyyy-MM-dd HH:mm}, snapshot abcdef01. Only changes are sent.", next);
    }
}
