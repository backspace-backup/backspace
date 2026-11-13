using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U230, U232, U233, U234 (owner, 12 November 2026).</summary>
public sealed class CheckLogReadabilityTests
{
    /// <summary>U230: "The timestamps are wrong no?" restic's runner wrote its
    /// time in UTC ("00:39:09.512Z") beside local times. The console drops it;
    /// the line's local time is in the Timestamps column.</summary>
    [Fact]
    public void A_restic_line_is_shown_without_its_UTC_time_and_keeps_its_colour()
    {
        var utc = new DateTime(2026, 11, 13, 0, 39, 9, 512, DateTimeKind.Utc);
        var raw = LogLineStamp.Stamp("[restic] 00:39:09.512Z\terror\tcheck finished: exit code 1 (failed) after 8.5 s", utc);
        var line = ConsoleLogParser.ParseText(raw).Single();
        Assert.Equal("[restic] error\tcheck finished: exit code 1 (failed) after 8.5 s", line.Text);
        Assert.Equal(ConsoleLineKind.Error, line.Kind);
        Assert.Equal(utc.ToLocalTime(), line.Time);
        // The tag and level stay quiet.
        Assert.Contains(ConsoleLineClassifier.Spans(line.Text, line.Kind), s => s.Kind == ConsoleLineClassifier.SpanKind.Tag && s.Start == 0 && s.Length == "[restic] error".Length);
        // Live, unstamped: same.
        var live = new ConsoleLogParser().Feed("[restic] 00:39:09.512Z\tdebug\tcommand: restic snapshots", DateTime.Now)!;
        Assert.Equal("[restic] debug\tcommand: restic snapshots", live.Text);
        Assert.True(live.IsDetail);
        // Other lines are untouched, even one that names a time.
        Assert.Equal("[backspace] Next run at 00:39:09Z", ConsoleLineClassifier.WithoutRecordTime("[backspace] Next run at 00:39:09Z"));
    }

    /// <summary>U233 (owner: "The individual file restore results should be
    /// only in verbose"): a restore check's per-file lines are detail; a file
    /// that didn't come back as saved stays visible, red.</summary>
    [Fact]
    public void A_restore_checks_file_list_is_shown_with_Verbose_only()
    {
        var ok = ConsoleLogParser.ParseText("[Recovered] Google Drive - backspace-952aab70 / D:\\ / ComfyUI/INSTALLER — 4/4 bytes: File recovered with the expected size. Comparison with today's original was not requested.").Single();
        Assert.True(ok.IsDetail);
        var bad = ConsoleLogParser.ParseText("[SizeMismatch] USB / D:\\ / a.bin — 3/4 bytes: Restored size differs.").Single();
        Assert.Equal(ConsoleLineKind.Error, bad.Kind);
        Assert.False(bad.IsDetail);
        Assert.True(ConsoleLogParser.ParseText("=== Every file checked ===").Single().IsDetail);
        Assert.True(ConsoleLogParser.ParseText("[file] Recovered · USB / a.bin · 4 bytes").Single().IsDetail);
    }

    /// <summary>U233: a check's log names it, not "check" or "backup".</summary>
    [Fact]
    public void A_check_log_is_headed_with_the_check()
    {
        var text = string.Join("\n",
            ConsoleLogParser.HeaderMarker,
            "backup        : local_d",
            "operation     : Data check (1 GiB sample)",
            "destination   : Google Drive - backspace-952aab70",
            "started (UTC) : 2026-11-13T00:38:22.0000000Z",
            "",
            "2026-11-13T00:46:30.000Z [skip] Data check (1 GiB sample) of «USB» waits: The drive is not connected.");
        var section = ConsoleLogParser.ParseText(text).First(l => l.Kind == ConsoleLineKind.Section);
        Assert.Equal("local_d · Data check (1 GiB sample)", section.Text);
        Assert.Equal("Google Drive - backspace-952aab70", section.Subtitle);
    }

    [Fact]
    public void A_check_result_line_says_how_it_ended()
    {
        Assert.Equal("[recap] Data check (1 GiB sample) passed on «Google Drive» in 8 min 8 s.",
            BackupOrchestrator.ResultLine("Data check (1 GiB sample)", RunKind.Check, "Google Drive", BackupRunStatus.Success, "x", TimeSpan.FromSeconds(488)));
        var failed = BackupOrchestrator.ResultLine("Structure check", RunKind.Check, "USB", BackupRunStatus.Failed, "Pack 1a2b is damaged.\nmore", TimeSpan.FromSeconds(42));
        Assert.Equal("[error] Structure check failed on «USB» after 42 s: Pack 1a2b is damaged.", failed);
        Assert.Equal(ConsoleLineKind.Error, ConsoleLineClassifier.Classify(failed));
        Assert.Equal(ConsoleLineKind.Summary, ConsoleLineClassifier.Classify("[recap] Data check passed on «USB» in 1 min 0 s."));
    }

    /// <summary>U233: progress in the log at each step and tenth, not each second.</summary>
    [Fact]
    public void Progress_reaches_the_log_at_each_step_and_tenth_only()
    {
        var said = new List<string>();
        var clock = new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
        var lines = new MaintenanceRunLog.ProgressLines(said.Add, () => clock);
        lines.Report("Preparing", "Preparing the files to restore", "Google Drive", -1);
        for (var p = 0; p <= 100; p++) { clock = clock.AddSeconds(1); lines.Report("Restoring", $"Restoring · {p} of 100 files", "Google Drive", p); }
        foreach (var s in said) Assert.StartsWith("[progress] ", s);
        Assert.Equal("[progress] Preparing the files to restore · Google Drive", said[0]);
        // 0%, then 10% … 100%: eleven, not 101.
        Assert.Equal(12, said.Count);
        Assert.Equal("[progress] 50% · Restoring · 50 of 100 files · Google Drive", said[6]);
        // Stuck on one step: a line a minute at most, only when it moved.
        said.Clear();
        lines.Report("Restoring", "same", "", 100);
        clock = clock.AddMinutes(5);
        lines.Report("Restoring", "same", "", 100);
        Assert.Empty(said);
    }

    /// <summary>U234: "the progress bar has no %, only 'Preparing the files to
    /// restore'". In the background service's worker (no app, no UI loop) a
    /// restore check's progress was posted to a UI thread that never ran.</summary>
    [Fact]
    public async Task Restore_progress_applies_at_once_where_there_is_no_app()
    {
        var registry = new ActiveRestoreRegistry();
        var previous = ActiveRestoreRegistry.RunsWithoutUi;
        ActiveRestoreRegistry.RunsWithoutUi = () => true;
        try
        {
            var session = await Task.Run(() =>
            {
                var s = registry.BeginTest(new Backup { Name = "local_d" }, "Restore check", DateTime.UtcNow, null);
                registry.UpdateProgress(s, 2_922, 1_215, 1_000_000, 400_000);
                return s;
            });
            Assert.Same(session, registry.RunningTestFor(session.BackupId));
            Assert.False(session.IsProgressIndeterminate);
            Assert.Equal(1_215, session.RestoredCount);
            Assert.Equal(40, session.PercentComplete * 100, 3);
        }
        finally { ActiveRestoreRegistry.RunsWithoutUi = previous; }
    }

    /// <summary>U232: each run reads as what it was, when, and how it ended.</summary>
    [Fact]
    public void Run_picker_rows_are_short_and_say_what_each_run_was()
    {
        var now = new DateTime(2026, 11, 12, 15, 0, 0, DateTimeKind.Local);
        RunPickerRow Row(RunRecord r) => RunPickerRow.From(r, now);
        DateTime At(int day, int h, int m) => new DateTime(2026, 11, day, h, m, 0, DateTimeKind.Local).ToUniversalTime();

        var data = Row(new RunRecord { Kind = RunKind.Check, CheckKind = CheckKind.DataSample, StartedUtc = At(12, 14, 38), EndedUtc = At(12, 14, 38).AddSeconds(488),
            Status = BackupRunStatus.Success, Summary = "Data check (1 GiB sample) passed in 8 min 8 s." });
        Assert.Equal(("Data check (1 GiB sample)", "Today 14:38", "Passed · 8 min 8 s", ""), (data.Title, data.When, data.Outcome, data.Detail));
        Assert.True(data.IsOk);

        var running = Row(new RunRecord { Kind = RunKind.Check, StartedUtc = At(12, 14, 38), Status = BackupRunStatus.Running,
            Summary = "Running: Restore check (1% of files, at most 1 GiB) (2 of 2)" });
        Assert.Equal(("Restore check (1% of files, at most 1 GiB)", "Running"), (running.Title, running.Outcome));
        Assert.True(running.IsRunning);

        var backup = Row(new RunRecord { Kind = RunKind.Backup, StartedUtc = At(11, 18, 27), EndedUtc = At(11, 18, 29).AddSeconds(6),
            Status = BackupRunStatus.Success, Summary = "296 B uploaded · in 2m 06s" });
        Assert.Equal(("Backup", "Yesterday 18:27", "Completed · 2 min 6 s", "296 B uploaded"), (backup.Title, backup.When, backup.Outcome, backup.Detail));

        var locked = Row(new RunRecord { Kind = RunKind.Check, CheckKind = CheckKind.DataSample, StartedUtc = At(6, 18, 42), EndedUtc = At(6, 18, 44),
            Status = BackupRunStatus.Failed,
            Summary = "Structure check + data sample (1 GiB) did not pass: unable to create lock in backend: repository is already locked\nlock was created at …" });
        Assert.Equal("Fri 18:42", locked.When);
        Assert.True(locked.IsFail);
        Assert.DoesNotContain("\n", locked.Detail);
        Assert.Contains("lock was created", locked.Tooltip);

        var old = Row(new RunRecord { Kind = RunKind.Prune, StartedUtc = At(1, 6, 0), EndedUtc = At(1, 6, 1), Status = BackupRunStatus.Skipped, Summary = "Cleanup skipped: a restore is reading this backup." });
        Assert.Equal(("Cleanup", "1 Nov 2026 06:00"), (old.Title, old.When));
        Assert.True(old.IsWarn);
    }

    /// <summary>U232: a restore check run as a check is listed once; older
    /// records without a check log keep both.</summary>
    [Fact]
    public void A_restore_checks_own_record_is_folded_into_its_check()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        var check = new RunRecord { Kind = RunKind.Check, CheckKind = CheckKind.TestRestore, StartedUtc = t, EndedUtc = t.AddMinutes(20), LogPath = "c.log", Status = BackupRunStatus.Success };
        var own = new RunRecord { Kind = RunKind.TestRestore, StartedUtc = t.AddSeconds(30), EndedUtc = t.AddMinutes(19), Status = BackupRunStatus.Success };
        var earlier = new RunRecord { Kind = RunKind.TestRestore, StartedUtc = t.AddDays(-1), EndedUtc = t.AddDays(-1).AddMinutes(5), Status = BackupRunStatus.Success };
        var shown = LogsViewModel.WithoutFoldedRestoreRecords([check, own, earlier]);
        Assert.Equal(new[] { check, earlier }, shown);
        check.LogPath = "";
        Assert.Equal(3, LogsViewModel.WithoutFoldedRestoreRecords([check, own, earlier]).Count);
    }
}
