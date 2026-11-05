using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U144 (5 November 2026): restic's percentage is of the whole source,
/// so an incremental backup races to 99% and then waits on its changes. The
/// progress now follows the work, from the changes Backspace finds itself.</summary>
public sealed class ChangePlanTests
{
    private static JsonElement Status(long bytesDone, long totalBytes, long filesDone, long totalFiles, string? current)
        => JsonSerializer.SerializeToElement(new
        {
            lastStatus = new
            {
                status = new
                {
                    percentDone = totalBytes == 0 ? 0 : bytesDone / (double)totalBytes, totalFiles = totalFiles.ToString(), totalBytes = totalBytes.ToString(),
                    filesDone = filesDone.ToString(), bytesDone = bytesDone.ToString(), currentFile = current is null ? Array.Empty<string>() : new[] { current },
                },
            },
        });

    [Fact]
    public void Files_are_in_restics_order_depth_first_with_names_in_byte_order()
    {
        // restic: sort.Strings on each folder's names, then into each folder in turn.
        var paths = new[] { "a-b", "a/z", "a/b/c", "B", "a b", "a", "_x", "a/b" };
        var sorted = paths.OrderBy(p => p, Comparer<string>.Create(ChangePlan.ResticOrder)).ToArray();
        Assert.Equal(new[] { "B", "_x", "a", "a/b", "a/b/c", "a/z", "a b", "a-b" }, sorted);
    }

    [Fact]
    public void Changes_are_the_files_whose_size_or_date_differ_from_the_last_backup()
    {
        var now = new[]
        {
            new ChangePlan.FileState("docs/same.txt", 100, 1),
            new ChangePlan.FileState("docs/grown.txt", 300, 1),
            new ChangePlan.FileState("docs/touched.txt", 100, 9),
            new ChangePlan.FileState("new.bin", 5_000, 1),
        };
        var before = new Dictionary<ulong, (long, long)>
        {
            [ChangePlan.Hash("docs/same.txt")] = (100, 1),
            [ChangePlan.Hash("DOCS/grown.txt")] = (200, 1), // case does not matter on Windows
            [ChangePlan.Hash("docs/touched.txt")] = (100, 1),
        };
        var plan = new ChangePlan(@"C:\src", now, before);
        Assert.Equal(4, plan.Files);
        Assert.Equal(3, plan.ChangedFiles);
        Assert.Equal(1, plan.UnchangedFiles);
        Assert.Equal(5_400, plan.ChangedBytes);
        // Order: docs/grown, docs/same, docs/touched, new.bin.
        Assert.Equal((0, 0), plan.Done(@"C:\src\docs\grown.txt", 0, 0));
        Assert.Equal((0, 150), plan.Done(@"C:\src\docs\grown.txt", 0, 150)); // half of the grown file read
        Assert.Equal((1, 300), plan.Done(@"C:\src\docs\touched.txt", 2, 400));
        Assert.Equal((1, 400 + 2_000), plan.Done(@"C:\src\new.bin", 3, 2_500));
        // Not in the list (a file restic sees and Backspace does not): its count of files done.
        Assert.Equal((1, 300), plan.Done(@"C:\elsewhere\x", 2, 400));
    }

    [Fact]
    public void The_listing_is_saved_for_the_snapshot_it_belongs_to()
    {
        using var ws = new TempWorkspace("change-lists");
        var store = new ChangePlanStore(ws.Sub("lists"));
        var files = new[] { new ChangePlan.FileState("a.txt", 10, 5), new ChangePlan.FileState("b/c.txt", 20, 6) };
        store.Write("plan-1", "abc123", 1_200, 3_000_000, files);
        var saved = store.Read("plan-1")!;
        Assert.Equal("abc123", saved.SnapshotId);
        Assert.Equal((1_200.0, 3_000_000.0), (saved.FilesPerSecond, saved.BytesPerSecond));
        Assert.Equal((10L, 5L), saved.Files[ChangePlan.Hash("a.txt")]);
        Assert.Equal((20L, 6L), saved.Files[ChangePlan.Hash("b/c.txt")]);
        Assert.Null(store.Read("plan-2"));
        File.WriteAllText(Directory.GetFiles(ws.Sub("lists")).Single(), "damaged");
        Assert.Null(store.Read("plan-1")); // a damaged cache is ignored, not trusted
    }

    [Fact]
    public void A_plan_is_only_used_against_the_snapshot_restic_compares_with()
    {
        using var ws = new TempWorkspace("change-plan");
        var source = ws.Sub("src");
        File.WriteAllText(Path.Combine(source, "a.txt"), "hello");
        var store = new ChangePlanStore(ws.Sub("lists"));
        var rules = new SourceSizeRules(null, false);
        var first = ChangePlanner.Start(store, "plan", source, rules, previousSnapshotId: null, CancellationToken.None);
        first.SaveAsync("snap-1", (1_000, 1_000_000)).Wait();
        File.WriteAllText(Path.Combine(source, "b.txt"), "new file");
        var matching = ChangePlanner.Start(store, "plan", source, rules, "snap-1", CancellationToken.None);
        var other = ChangePlanner.Start(store, "plan", source, rules, "snap-0", CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => matching.Plan is not null, 10_000));
        Assert.Equal(1, matching.Plan!.ChangedFiles);
        Assert.Equal(8, matching.Plan.ChangedBytes);
        Assert.Equal((1_000.0, 1_000_000.0), matching.PreviousPace);
        Thread.Sleep(500);
        Assert.Null(other.Plan); // another snapshot: progress as before
    }

    /// <summary>local_c's run of 5 November, shape kept: 600,000 files, 190 GB,
    /// about 9 GB of changes, most of them one large file near the end.</summary>
    [Fact]
    public void The_percentage_and_time_left_follow_the_work_not_the_source_size()
    {
        var files = new List<ChangePlan.FileState>();
        var before = new Dictionary<ulong, (long, long)>();
        for (var i = 0; i < 600_000; i++)
        {
            var path = $"d{i / 1000:D4}/f{i % 1000:D4}";
            files.Add(new(path, 300_000, 1));
            before[ChangePlan.Hash(path)] = (300_000, 1);
        }
        files.Add(new("d0550/zz-large.vhdx", 9_000_000_000, 2)); // the one large change, after 551,000 files
        var plan = new ChangePlan(@"C:\", files, before);
        Assert.Equal(9_000_000_000, plan.ChangedBytes);
        var total = plan.TotalBytes;
        var live = new BackupLiveProgress(() => 0, null, () => plan, () => null);
        var start = new DateTime(2026, 11, 5, 10, 34, 0, DateTimeKind.Utc);
        string? atCheckEnd = null, sending = null, sendingLater = null; var trace = "";
        long filesDone = 0, bytesDone = 0;
        for (var t = 0; t <= 3_600; t += 5)
        {
            string current;
            if (filesDone < 551_000)
            {
                filesDone = Math.Min(551_000, filesDone + 1_500 * 5); // unchanged files at 1,500 a second
                bytesDone = filesDone * 300_000;
                current = $@"C:\d{filesDone / 1000:D4}\f{filesDone % 1000:D4}";
            }
            else
            {
                bytesDone = Math.Min(551_000L * 300_000 + 9_000_000_000, bytesDone + 5 * 3_800_000L); // the large file at 3.8 MB/s
                current = @"C:\d0550\zz-large.vhdx";
            }
            var line = live.Next(Status(bytesDone, total, filesDone, 600_001, current), start.AddSeconds(t));
            if (t == 365) atCheckEnd = line;
            if (t >= 355 && t <= 385) trace += t + ": " + line + " | ";
            if (t == 600) sending = line;
            if (t == 2_000) sendingLater = line;
        }
        // Checking 551,000 unchanged files is a small part of the work: not 91%.
        var pct = double.Parse(atCheckEnd![11..atCheckEnd.IndexOf('%')], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(pct is >= 5 and <= 40, trace);
        Assert.True(atCheckEnd.Contains("changes 0 B / ~8.4 GB"), trace);
        Assert.True(sending!.Contains(" files, 176 GB"), sending); // restic's total, the source's size
        // 230 s into the large file at 3.8 MB/s, 8.1 GB to go: about 36 min.
        Assert.True(sending.Contains("about 3") && sending.EndsWith("min left"), sending);
        var later = double.Parse(sendingLater![11..sendingLater.IndexOf('%')], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(later > pct + 30, sendingLater);
        Assert.NotNull(live.MeasuredPace);
        Assert.InRange(live.MeasuredPace!.Value.BytesPerSecond, 3_000_000, 4_600_000);
    }

    [Fact]
    public void Without_a_plan_the_line_is_as_before()
    {
        var live = new BackupLiveProgress(() => 0, null, () => null, () => null);
        var line = live.Next(Status(5_000_000_000, 50_000_000_000, 1_000, 10_000, null), new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("[progress] 0.0% · Backing up · 4.7 GB · 1,000 / 10,000+ files", line);
    }

    [Fact]
    public void The_source_shows_the_counted_size_from_the_new_line_too()
    {
        Assert.Equal("190 GB", BackupLiveProgress.CountedTotal("[progress] 23.4% · Backing up · changes 3.1 / ~9.0 GB · 504,701 / 601,922 files, 190 GB · 881 MB sent at 3.8 MB/s · about 25 min left"));
        // "~": Backspace's own listing of the source for this run, which is its count with --no-scan (U145).
        Assert.Equal("190 GB", BackupLiveProgress.CountedTotal("[progress] 2.0% · Backing up · changes 0 / ~9.0 GB · 4,701 / ~601,922 files, ~190 GB"));
    }
}

/// <summary>U142: each backup in a backup's activity says what it added.</summary>
public sealed class RunAddedTests
{
    private const string Recap = "[recap] Backup complete: 601,922 files (190 GB) checked · 14,145 new, 239 changed, 587,538 unchanged · 12 GB added to the storage · took 59m 40s · snapshot a4042f53";

    [Fact]
    public void The_figures_come_from_restics_summary_or_its_recap_line()
    {
        var op = JsonSerializer.SerializeToElement(new { lastStatus = new { summary = new { filesNew = "476", filesChanged = "157", dataAdded = "1717986918", totalDuration = 1154.0 } } });
        Assert.Equal((476, 157, 1_717_986_918L), BackupLiveProgress.Stats(op));
        Assert.Equal((14_145, 239, 12L << 30), BackupLiveProgress.StatsFromRecap(Recap));
        Assert.Null(BackupLiveProgress.StatsFromRecap("[progress] 12% · Backing up"));
    }

    [Fact]
    public void Activity_rows_show_what_each_backup_added_even_for_runs_saved_before()
    {
        using var ws = new TempWorkspace("run-added");
        var log = Path.Combine(ws.Root, "run.log");
        File.WriteAllLines(log, new[] { "[setup] 273 exclusion rules loaded.", Recap, "[backspace] Finishing snapshot history" });
        var recorded = new RunRecord { BackupId = "b", Status = BackupRunStatus.Success, FilesNew = 476, FilesModified = 157, BytesUploaded = 1_717_986_918 };
        var older = new RunRecord { BackupId = "b", Status = BackupRunStatus.Success, LogPath = log };
        var failed = new RunRecord { BackupId = "b", Status = BackupRunStatus.Failed, LogPath = log };
        Assert.Equal("476 new · 157 changed · 1.6 GB added", Backspace.ViewModels.BackupHistoryViewModel.Added(recorded));
        Assert.Equal("14,145 new · 239 changed · 12 GB added", Backspace.ViewModels.BackupHistoryViewModel.Added(older));
        Assert.Equal("", Backspace.ViewModels.BackupHistoryViewModel.Added(failed));

        // A background run: the service reads its own log and sends the figures, not the path.
        var protocol = new Backspace.Services.Platform.Windows.ServiceHistoryProtocol(_ => new List<RunRecord> { older });
        var reply = JsonSerializer.Deserialize<Backspace.Services.Platform.Windows.ServiceHistoryProtocol.Response>(
            protocol.Dispatch(JsonSerializer.SerializeToUtf8Bytes(new Backspace.Services.Platform.Windows.ServiceHistoryProtocol.Request(1, "b"), Backspace.Services.Platform.Windows.ServiceRunAuthorization.Json)),
            Backspace.Services.Platform.Windows.ServiceRunAuthorization.Json)!;
        var sent = Assert.Single(reply.Runs);
        Assert.Equal("", sent.LogPath);
        Assert.Equal((14_145, 239, 12L << 30), (sent.FilesNew, sent.FilesModified, sent.BytesUploaded));
    }
}
