using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U143: what a backup added, from its snapshot and the one before.</summary>
public sealed class RunChangesTests
{
    [Fact]
    public void The_snapshot_is_read_from_the_last_recap_only()
    {
        const string log = "[setup] Previous backup to «Drive»: 2026-11-04 20:00, snapshot 11111111. Only changes are sent.\n"
            + "[progress] 99.0% · Backing up\n"
            + "[recap] Backup complete: 601,922 files (190 GB) checked · 14,145 new, 239 changed, 587,538 unchanged · 12 GB added to the storage · took 59m 40s · snapshot a4042f53\n";
        Assert.Equal("a4042f53", RunChanges.SnapshotOf(log));
        // The previous backup named in the setup line is not this one's.
        Assert.Null(RunChanges.SnapshotOf("[setup] Previous backup to «Drive»: 2026-11-04 20:00, snapshot 11111111."));
        Assert.Null(RunChanges.SnapshotOf(""));
        // Saved lines start with the time they were written (U176), Windows line ends included.
        var stamped = string.Join("\r\n", log.TrimEnd('\n').Split('\n')
            .Select((line, i) => LogLineStamp.Stamp(line, new DateTime(2026, 11, 10, 6, 0, i, DateTimeKind.Utc)))) + "\r\n";
        Assert.Equal("a4042f53", RunChanges.SnapshotOf(stamped));
        Assert.Null(RunChanges.SnapshotOf(LogLineStamp.Stamp("[setup] Previous backup: snapshot 11111111.", DateTime.UtcNow)));
    }

    [Fact]
    public void New_and_changed_files_are_found_by_size_and_date_and_others_are_not()
    {
        var t = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc);
        var before = new[] { new RevisionFile("a/same.txt", 10, t), new RevisionFile("a/grown.txt", 10, t), new RevisionFile("a/touched.txt", 10, t), new RevisionFile("gone.txt", 5, t) };
        var after = new[] { new RevisionFile("a/same.txt", 10, t), new RevisionFile("a/grown.txt", 12, t), new RevisionFile("a/touched.txt", 10, t.AddSeconds(1)), new RevisionFile("b/new.bin", 100, t), new RevisionFile("A/SAME.TXT", 10, t) };
        var changes = RunChanges.Compare(before, after);
        Assert.Equal(new[] { "a/grown.txt", "a/touched.txt", "b/new.bin", "A/SAME.TXT" }, changes.Select(c => c.Path));
        Assert.True(changes.Single(c => c.Path == "b/new.bin").IsNew);
        Assert.False(changes.Single(c => c.Path == "a/grown.txt").IsNew);
        // A removed file is not something the backup added.
        Assert.DoesNotContain(changes, c => c.Path == "gone.txt");
        // A first backup: everything is new.
        Assert.All(RunChanges.Compare(null, after), c => Assert.True(c.IsNew));
    }

    [Fact]
    public void The_folder_tree_sums_bytes_and_opens_where_changes_branch_out()
    {
        var root = ChangeNode.Build("local_c", new[]
        {
            new ChangedFile("C:/Users/me/Videos/v.mp4", 1000, true),
            new ChangedFile("C:/Users/me/Photos/2026/p1.jpg", 300, true),
            new ChangedFile("C:/Users/me/Photos/2026/p2.jpg", 200, false),
        });
        Assert.Equal(1500, root.Bytes);
        Assert.Equal(3, root.Files);
        var top = root.Collapsed();
        Assert.Equal("C:/Users/me", top.Path);
        Assert.Equal(500, top.Children.Single(c => c.Name == "Photos").Bytes);
        Assert.Equal("Photos/2026".Length, top.Children.Single(c => c.Name == "Photos").Collapsed().Path.Length - "C:/Users/me/".Length);
        // A folder holding a single file is not passed through.
        Assert.Equal("Videos", top.Children.Single(c => c.Name == "Videos").Collapsed().Name);
    }

    [Fact]
    public void The_treemap_fills_its_area_in_proportion_without_overlap()
    {
        var items = new (string, double)[] { ("a", 600), ("b", 250), ("c", 100), ("d", 40), ("e", 10), ("zero", 0) };
        var laid = Treemap.Layout(items, new Treemap.Box(0, 0, 400, 300));
        Assert.Equal(5, laid.Count); // nothing drawn for 0 bytes
        foreach (var (item, box) in laid)
        {
            var weight = items.Single(i => i.Item1 == item).Item2;
            Assert.Equal(400 * 300 * weight / 1000, box.W * box.H, 3);
            Assert.InRange(box.X, -1e-6, 400); Assert.InRange(box.X + box.W, 0, 400 + 1e-6);
            Assert.InRange(box.Y, -1e-6, 300); Assert.InRange(box.Y + box.H, 0, 300 + 1e-6);
        }
        for (var i = 0; i < laid.Count; i++)
            for (var j = i + 1; j < laid.Count; j++)
            {
                var (p, q) = (laid[i].Box, laid[j].Box);
                var overlap = Math.Max(0, Math.Min(p.X + p.W, q.X + q.W) - Math.Max(p.X, q.X)) * Math.Max(0, Math.Min(p.Y + p.H, q.Y + q.H) - Math.Max(p.Y, q.Y));
                Assert.True(overlap < 1e-6, $"{laid[i].Item} overlaps {laid[j].Item}");
            }
        // Squarified: the largest box is not a sliver.
        var big = laid.Single(l => l.Item == "a").Box;
        Assert.InRange(Math.Max(big.W / big.H, big.H / big.W), 1, 2);
    }
}

/// <summary>U138: the version-3 service's history and logs are read from the
/// portable folder, with no pipe and without starting the service.</summary>
public sealed class ServiceHistoryFilesTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    [Fact]
    public void Service_runs_are_read_with_their_logs_even_after_the_folder_moved()
    {
        var root = Path.Combine(AppPaths.ConfigRoot, "u138-" + Guid.NewGuid().ToString("N")[..8]);
        var folder = Path.Combine(Backspace.Services.Platform.Windows.SystemService.DataDirectoryFor(root), "logs", "engine", "local_c");
        Directory.CreateDirectory(folder);
        try
        {
            var log = Path.Combine(folder, "20261105-103350-cell.log");
            File.WriteAllText(log, "[recap] Backup complete: 1 files (1 KB) checked · 1 new, 0 changed, 0 unchanged · 1 KB added to the storage · took 1s · snapshot 0123abcd\n");
            // Recorded under the folder's old place: only the backup folder and name still match.
            var recorded = Path.Combine(@"D:\Old\Backspace.config\service\logs\engine\local_c", Path.GetFileName(log));
            var runId = Guid.NewGuid().ToString("N");
            RunRecord Run(string id, string backup, DateTime started, string logPath) => new()
            {
                Id = id, BackupId = backup, BackupName = "local_c", Kind = RunKind.Backup, Status = BackupRunStatus.Success,
                StartedUtc = started, EndedUtc = started.AddMinutes(1), LogPath = logPath, FilesNew = 1, BytesUploaded = 1024,
                Cells = { new CellOutcome { SourcePath = @"C:\", DestinationName = "Drive", Status = BackupRunStatus.Success, LogPath = logPath } },
            };
            var now = DateTime.UtcNow;
            // The service keeps its latest runs in history.json and every run, compactly, in timeline.json.
            File.WriteAllText(Path.Combine(folder, "history.json"), JsonSerializer.Serialize(new[]
            {
                Run(runId, "b1", now, recorded),
                Run(Guid.NewGuid().ToString("N"), "other", now, recorded),
                Run(Guid.NewGuid().ToString("N"), "b1", now.AddHours(-1), @"D:\Old\Backspace.config\service\logs\engine\local_c\gone.log"),
                Run(Guid.NewGuid().ToString("N"), "b1", now.AddHours(-2), @"C:\Windows\win.ini"),
            }, Json));
            var oldest = new RunRecord { Id = Guid.NewGuid().ToString("N"), BackupId = "b1", BackupName = "local_c", Kind = RunKind.Backup,
                Status = BackupRunStatus.Success, StartedUtc = now.AddDays(-60), EndedUtc = now.AddDays(-60), FilesNew = 7, BytesUploaded = 5000 };
            File.WriteAllText(Path.Combine(folder, "timeline.json"), JsonSerializer.Serialize(new[] { oldest }, Json));

            var runs = Backspace.Services.Platform.Windows.ServiceHistoryFiles.SystemServiceRuns(root, "b1");
            Assert.Equal(4, runs.Count); // the other backup's run in the same folder is not taken
            var latest = runs.Single(r => r.Id == runId);
            Assert.Equal(log, latest.LogPath);
            Assert.Equal(log, latest.Cells.Single().LogPath);
            Assert.Equal("0123abcd", RunChanges.SnapshotOf(latest, latest.Cells.Single()));
            // A log that is gone keeps its path, so the log view says so; a
            // path that is not a .log is never handed on.
            Assert.False(File.Exists(runs.Single(r => r.StartedUtc == now.AddHours(-1)).LogPath));
            Assert.Equal("", runs.Single(r => r.StartedUtc == now.AddHours(-2)).LogPath);
            // A run past the latest ones still shows, with what it added.
            var old = runs.Single(r => r.Id == oldest.Id);
            Assert.Equal((7, 5000L), (old.FilesNew, old.BytesUploaded));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
