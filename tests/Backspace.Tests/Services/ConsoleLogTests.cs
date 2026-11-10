using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Collections;
using Backspace.Services;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U176: the Activity &amp; logs console colours each line by its type. The
/// lines below are copied from real run logs (paths and names changed), one
/// group per kind, with the near misses that must not be taken for it.
/// </summary>
public sealed class ConsoleLogTests
{
    private const string Plan = "\"fa743d1508bc40229f505dc6beaf6288-952aab7048b54646b8a9f700dbdb6ccd-target-952aab70-28f709d7\"";

    public static TheoryData<string> SetupLines => new()
    {
        "[setup] Previous backup to «Google Drive - backspace-952aab70»: 2026-11-08 20:00, snapshot 7ee22f0f. Only changes are sent.",
        "[setup] 259 exclusion rules loaded; files kept only in the cloud are skipped.",
        "[setup] Shadow copy on: open and locked files are read from a snapshot of the drive.",
        "[orchestrator] attempt 1 starting at 2026-11-10T06:00:00.5737522Z",
        "[backrest] op=80 status=STATUS_SUCCESS",
        $"[tasklog] 20:10:03.139Z\tinfo\tbackup for plan {Plan}\tbackup complete\t{{\"plan\": {Plan}}}",
        $"[tasklog] 20:10:03.141Z\tinfo\tbackup for plan {Plan}\ttask finished\t{{\"duration\": 599.5595433}}",
    };

    public static TheoryData<string> ProgressLines => new()
    {
        "[progress] 0.0% · Backing up · 129 of ~422,166 files checked (~176 GB) · 0 B of ~17 GB changed files backed up · 461 B of new data uploaded",
        "[progress] 4.7% · Backing up · 18,483 of ~422,166 files checked (~176 GB) · 19 MB of ~17 GB changed files backed up · 5.4 MB of new data uploaded, 273 KB/s · about 9 min left",
        "[progress] 100% · Backing up · 1 of 1 files checked",
    };

    public static TheoryData<string> SummaryLines => new()
    {
        "[recap] Backup complete: 375,523 files (175 GB) checked · 26,969 new, 3,212 changed, 345,342 unchanged · 1.8 GB added to the storage · took 9m 39s · snapshot 4bd48ee6",
        "[backspace] 1 online-only file skipped: kept only in the cloud, not downloaded and not in this backup",
        "[backspace] 1,204 online-only files skipped: kept only in the cloud, not downloaded and not in this backup",
        "[cleanup] Nothing to remove: the keep rules keep every backup.",
        "[cleanup] 2 old backups removed · 1.4 GB freed in the storage",
        "[restore] done: status=Success 12/12",
    };

    public static TheoryData<string> WarningLines => new()
    {
        "[backrest] op=4 status=STATUS_WARNING Partial backup, some files may not have been read completely.",
        "[backrest] op=9 status=STATUS_USER_CANCELLED",
        "[restic] {\"item\":\"C:\\\\Users\\\\sam\\\\Apps\\\\Phone\\\\calls.xml\",\"during\":\"archival\",\"message\":\"C:\\\\Users\\\\sam\\\\Apps\\\\Phone\\\\calls.xml: unsupported file type \\\"irregular\\\"\"}",
        $"[tasklog] 00:34:35.623Z\twarn\tbackup for plan {Plan}\terror processing item\t{{\"item\": \"C:\\\\Users\\\\sam\\\\Apps\\\\Phone\\\\sms.xml\"}}",
        "[orchestrator] Files that other apps had open may have been skipped, because your latest changes weren't applied to the background service yet. Open Settings → Administrator access and choose Try again to include them.",
        "[orchestrator] retry 2/3 after 30s backoff…",
        "[orchestrator] transient retry (540s of budget left): connection reset by peer",
        "[orchestrator] tolerable skip: the drive is not connected",
        "[backspace] Cleanup waits: a snapshot is mounted as drive S:. It runs after a later backup, once the drive is unmounted and restores have finished.",
        "[backspace] The list of changed files is incomplete: restic stopped with code 1.",
        "[backspace] The retention policy could not be read, so no backup was removed: bad JSON",
        "[backrest] The full operation log could not be retrieved. Structured file errors above are retained.",
        "[skip] no snapshots at this destination for C:\\Users\\sam\\Documents",
        "2026-04-30 14:00:02 WARN CHUNK_RETRY retrying upload",
    };

    public static TheoryData<string> ErrorLines => new()
    {
        "[orchestrator] attempt 1 failed: Backup result: STATUS_ERROR. repository does not exist",
        "[orchestrator] non-retriable failure: Fatal: unable to open config file: stat C:\\backup\\config: The system cannot find the file specified.",
        "[orchestrator] transient retry budget exhausted: dial tcp: i/o timeout",
        "[backrest] op=12 status=STATUS_ERROR repository does not exist",
        $"[tasklog] 06:01:02.000Z\terror\tbackup for plan {Plan}\ttask failed\t{{\"error\": \"exit status 1\"}}",
        "[restore] error: access denied",
        "[restore] failed: the destination is offline",
        "[restore] done: status=Failed 0/3",
        "[error] couldn't list revisions for C:\\Users\\sam: boom",
        "[err] Fatal: wrong password or no key found",
        "Fatal: unable to open repository at rclone:backspace:repo",
        "[could not read log file: The process cannot access the file]",
        "2026-04-30 14:00:03 ERROR UPLOAD_FAILED 500 Internal Server Error",
    };

    public static TheoryData<string> DetailLines => new()
    {
        "[restic] 20:04:46.637Z\tdebug\trclone: 2026/11/09 20:04:46 ERROR : data/ba/ba2a25: Post request put error: googleapi: Error 500",
        "[repo-manager] 20:00:03.582Z\tdebug\trepo orchestrator starting backup\t{\"repo\": \"952aab70-target-952aab70\"}",
        "[restic] 00:34:34.743Z\tdebug\t[]",
        "[backrest] op=77 status=STATUS_INPROGRESS",
        "[backrest] op=4 status=STATUS_PENDING",
        "2026-04-30 14:00:01 DEBUG LIST_FILES listing",
    };

    public static TheoryData<string> PlainLines => new()
    {
        "[backspace] Finishing snapshot history",
        "[backspace] Removing stored data no backup uses any more",
        "[backspace] Data check targets 524,288,000 bytes per repository; whole-pack sampling, metadata and retries can change actual traffic.",
        "[restore] starting 12 item(s)",
        "2026-04-30 14:00:04 INFO SNAPSHOT_INFO revision 12 created",
        "Snapshot saved. Backup completed successfully.",
    };

    [Theory, MemberData(nameof(SetupLines))]
    public void Setup_and_state_lines_are_dim(string line) => Assert.Equal(ConsoleLineKind.Setup, ConsoleLineClassifier.Classify(line));

    [Theory, MemberData(nameof(ProgressLines))]
    public void Progress_lines_are_progress(string line) => Assert.Equal(ConsoleLineKind.Progress, ConsoleLineClassifier.Classify(line));

    [Theory, MemberData(nameof(SummaryLines))]
    public void The_recap_and_summaries_take_the_accent(string line) => Assert.Equal(ConsoleLineKind.Summary, ConsoleLineClassifier.Classify(line));

    [Theory, MemberData(nameof(WarningLines))]
    public void Warnings_are_amber(string line) => Assert.Equal(ConsoleLineKind.Warning, ConsoleLineClassifier.Classify(line));

    [Theory, MemberData(nameof(ErrorLines))]
    public void Errors_are_red(string line) => Assert.Equal(ConsoleLineKind.Error, ConsoleLineClassifier.Classify(line));

    [Theory, MemberData(nameof(DetailLines))]
    public void Engine_debug_is_detail_shown_with_verbose_only(string line)
    {
        Assert.Equal(ConsoleLineKind.Detail, ConsoleLineClassifier.Classify(line));
        Assert.True(new ConsoleLine(line, ConsoleLineClassifier.Classify(line)).IsDetail);
    }

    [Theory, MemberData(nameof(PlainLines))]
    public void Ordinary_lines_are_plain(string line) => Assert.Equal(ConsoleLineKind.Plain, ConsoleLineClassifier.Classify(line));

    [Fact]
    public void File_section_notice_and_blank_lines()
    {
        Assert.Equal(ConsoleLineKind.File, ConsoleLineClassifier.Classify("[file] added: C:\\Users\\sam\\Documents\\notes.txt"));
        Assert.True(new ConsoleLine("[file] added: x", ConsoleLineKind.File).IsDetail);
        Assert.Equal(ConsoleLineKind.Section, ConsoleLineClassifier.Classify("[section] local_c · backup"));
        Assert.Equal(ConsoleLineKind.Notice, ConsoleLineClassifier.Classify("[log truncated: showing last 4 MB of ~9 MB; 5,000,000 bytes hidden at the top]"));
        Assert.Equal(ConsoleLineKind.Notice, ConsoleLineClassifier.Classify("[live-tail truncated to the last 80,000 lines; the run's log file has them all]"));
        Assert.Equal(ConsoleLineKind.Blank, ConsoleLineClassifier.Classify(""));
        Assert.Equal(ConsoleLineKind.Blank, ConsoleLineClassifier.Classify("   "));
    }

    [Fact]
    public void Words_inside_paths_and_names_do_not_make_a_warning_or_an_error()
    {
        // A folder named "Error reports" in a progress line or a file line.
        Assert.Equal(ConsoleLineKind.Progress, ConsoleLineClassifier.Classify("[progress] 3.1% · Backing up · C:\\Error reports\\WARNING.txt"));
        Assert.Equal(ConsoleLineKind.File, ConsoleLineClassifier.Classify("[file] changed: C:\\Users\\sam\\ERROR\\log.txt"));
        Assert.Equal(ConsoleLineKind.Summary, ConsoleLineClassifier.Classify("[recap] Backup complete: 1 files (178 B) checked · took 0m 00s · snapshot 446aecc3"));
        // Debug wins over the word ERROR inside it.
        Assert.Equal(ConsoleLineKind.Detail, ConsoleLineClassifier.Classify("[restic] 20:04:50.212Z\tdebug\tSave(<data/ba2a256626>) returned error, retrying after 962ms"));
    }

    [Fact]
    public void A_line_stamp_round_trips_and_is_optional()
    {
        var utc = new DateTime(2026, 11, 10, 6, 0, 0, 569, DateTimeKind.Utc);
        var stamped = LogLineStamp.Stamp("[setup] Shadow copy on", utc);
        Assert.Equal("2026-11-10T06:00:00.569Z [setup] Shadow copy on", stamped);
        Assert.True(LogLineStamp.TryParse(stamped, out var parsed, out var rest));
        Assert.Equal(utc, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal("[setup] Shadow copy on", rest);
        Assert.Equal("[setup] Shadow copy on", LogLineStamp.Strip(stamped));
        // Not stamps: the older engine's dated lines, Backrest's times, plain text.
        foreach (var line in new[] { "2026-04-30 14:00:00 INFO RUN_START", "[restic] 20:04:46.637Z\tdebug\tx", "short", "" })
        {
            Assert.False(LogLineStamp.TryParse(line, out _, out var same));
            Assert.Equal(line, same);
            Assert.Equal(line, LogLineStamp.Strip(line));
        }
    }

    [Fact]
    public void Verbose_filter_reads_through_the_stamp()
    {
        var utc = DateTime.UtcNow;
        Assert.True(VerboseLogFilter.IsDetailLine(LogLineStamp.Stamp("[restic] 20:04:46.637Z\tdebug\tx", utc)));
        Assert.True(VerboseLogFilter.IsDetailLine(LogLineStamp.Stamp("[backrest] op=77 status=STATUS_INPROGRESS", utc)));
        Assert.False(VerboseLogFilter.IsDetailLine(LogLineStamp.Stamp("[recap] Backup complete", utc)));
    }

    private static string CellLog(bool stamped, bool marker, string status = "Success", string? lastError = null)
    {
        var t = new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);
        string L(string line, int s) => stamped ? LogLineStamp.Stamp(line, t.AddSeconds(s)) : line;
        var lines = new List<string>
        {
            "--- Backspace cell log ---",
            "backup        : local_c",
            "source        : C:\\",
            "destination   : Google Drive - backspace (GoogleDrive)",
            "storage-name  : target-952aab70",
            "started (UTC) : 2026-11-10T06:00:00.5693671Z",
            "",
        };
        if (marker) lines.Add(L("[section] local_c · backup\tGoogle Drive - backspace", 0));
        lines.Add(L("[orchestrator] attempt 1 starting at 2026-11-10T06:00:00.5737522Z", 0));
        lines.Add(L("[setup] Shadow copy on: open and locked files are read from a snapshot of the drive.", 1));
        lines.Add(L("[progress] 4.7% · Backing up · 18,483 of ~422,166 files checked", 30));
        lines.Add(L("[restic] 20:04:46.637Z\tdebug\trclone: retry", 31));
        lines.Add(L("[recap] Backup complete: 375,523 files (175 GB) checked · took 9m 39s", 579));
        if (marker) lines.Add(L("[section] local_c · cleanup\tGoogle Drive - backspace", 600));
        lines.Add(L("[cleanup] Nothing to remove: the keep rules keep every backup.", 630));
        lines.Add("");
        lines.Add("--- cell outcome ---");
        lines.Add($"status        : {status}");
        lines.Add("attempts      : 1");
        lines.Add($"summary       : Backup result: STATUS_{status.ToUpperInvariant()}.");
        if (lastError is not null) lines.Add($"last error    : {lastError}");
        lines.Add("ended (UTC)   : 2026-11-10T06:10:30.2463398Z");
        return string.Join("\r\n", lines) + "\r\n";
    }

    [Fact]
    public void A_run_log_starts_with_its_section_header_and_marks_where_cleanup_starts()
    {
        var lines = ConsoleLogParser.ParseText(CellLog(stamped: true, marker: true));
        var sections = lines.Where(l => l.Kind == ConsoleLineKind.Section).ToList();
        Assert.Equal(2, sections.Count);
        // The file's header and the marker right after it make one header.
        Assert.Equal("local_c · backup", sections[0].Text);
        // Where it ran: the header's source and the marker's destination.
        Assert.Equal(@"C:\ → Google Drive - backspace", sections[0].Subtitle);
        Assert.False(sections[0].IsDetail);
        Assert.Same(lines[0], sections[0]);
        Assert.Equal(new DateTime(2026, 11, 10, 6, 0, 0, 569, DateTimeKind.Utc).ToLocalTime(), sections[0].Time!.Value, TimeSpan.FromSeconds(1));
        Assert.EndsWith(" · " + sections[0].Time!.Value.ToString("HH:mm"), sections[0].SectionTitle);
        Assert.Equal("local_c · cleanup", sections[1].Text);
        Assert.Equal(new DateTime(2026, 11, 10, 6, 10, 0, DateTimeKind.Utc).ToLocalTime(), sections[1].Time);
        // The header's fields are dim, and only with Verbose (the header says
        // them); no marker line is shown as text.
        Assert.All(lines.Skip(1).Take(5), l => Assert.Equal(ConsoleLineKind.Meta, l.Kind));
        Assert.All(lines.Skip(1).Take(6), l => Assert.True(l.IsDetail, l.Text + " shows without Verbose."));
        // The outcome's bookkeeping too, but not its status and summary.
        Assert.True(lines.Single(l => l.Text == "--- cell outcome ---").IsDetail);
        Assert.True(lines.Single(l => l.Text.StartsWith("attempts ")).IsDetail);
        Assert.True(lines.Single(l => l.Text.StartsWith("ended (UTC) ")).IsDetail);
        Assert.False(lines.Single(l => l.Text.StartsWith("status ")).IsDetail);
        Assert.False(lines.Single(l => l.Text.StartsWith("summary ")).IsDetail);
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("[section]") || l.Text.StartsWith("--- Backspace"));
        // Each stamped line has its own local time and no stamp in its text.
        var progress = lines.Single(l => l.Kind == ConsoleLineKind.Progress);
        Assert.StartsWith("[progress] 4.7%", progress.Text);
        Assert.Equal(new DateTime(2026, 11, 10, 6, 0, 30, DateTimeKind.Utc).ToLocalTime(), progress.Time);
        Assert.Equal(ConsoleLineKind.Summary, lines.Single(l => l.Text.StartsWith("[recap]")).Kind);
        Assert.Equal(ConsoleLineKind.Detail, lines.Single(l => l.Text.StartsWith("[restic]")).Kind);
        Assert.Equal(ConsoleLineKind.Summary, lines.Single(l => l.Text.StartsWith("status ")).Kind);
    }

    [Fact]
    public void An_older_log_without_markers_or_stamps_still_gets_its_header()
    {
        var lines = ConsoleLogParser.ParseText(CellLog(stamped: false, marker: false));
        var section = Assert.Single(lines, l => l.Kind == ConsoleLineKind.Section);
        Assert.Equal("local_c · backup", section.Text);
        Assert.Equal(@"C:\ → Google Drive - backspace", section.Subtitle); // the kind "(GoogleDrive)" left out
        Assert.NotNull(section.Time); // from "started (UTC)"
        Assert.All(lines.Where(l => l.Kind is ConsoleLineKind.Progress or ConsoleLineKind.Setup), l => Assert.Null(l.Time));
    }

    [Fact]
    public void A_failed_outcome_block_is_red()
    {
        var lines = ConsoleLogParser.ParseText(CellLog(stamped: true, marker: true, status: "Failed", lastError: "repository does not exist"));
        Assert.Equal(ConsoleLineKind.Error, lines.Single(l => l.Text.StartsWith("status ")).Kind);
        Assert.Equal(ConsoleLineKind.Error, lines.Single(l => l.Text.StartsWith("summary ")).Kind);
        Assert.Equal(ConsoleLineKind.Error, lines.Single(l => l.Text.StartsWith("last error ")).Kind);
        Assert.Equal(ConsoleLineKind.Meta, lines.Single(l => l.Text.StartsWith("attempts ")).Kind);
        // The error list names the error, not the status and summary that repeat it.
        Assert.Equal(new[] { "last error    : repository does not exist" },
            lines.Where(l => l.Kind == ConsoleLineKind.Error && l.ListAsError).Select(l => l.Text));
        Assert.Equal("repository does not exist", lines.Single(l => l.Text.StartsWith("last error")).Message);
        // Two attempts is worth seeing without Verbose.
        var retried = ConsoleLogParser.ParseText(CellLog(stamped: true, marker: true, status: "Failed").Replace("attempts      : 1", "attempts      : 3"));
        Assert.False(retried.Single(l => l.Text.StartsWith("attempts ")).IsDetail);
        var warning = ConsoleLogParser.ParseText(CellLog(stamped: true, marker: true, status: "Warning"));
        Assert.Equal(ConsoleLineKind.Warning, warning.Single(l => l.Text.StartsWith("status ")).Kind);
    }

    [Fact]
    public void A_live_stream_gets_one_header_per_run_check_and_cleanup()
    {
        var parser = new ConsoleLogParser();
        var now = DateTime.Now;
        var shown = new[]
        {
            "[section] Documents · backup\tUSB drive", "[setup] Shadow copy off", "[recap] Backup complete",
            "[section] Documents · cleanup\tUSB drive", "[cleanup] Nothing to remove",
            "[section] Photos · backup\tNAS", "[progress] 1.0% · Backing up",
            "[section] Photos · check\tNAS", "[backrest] op=5 status=STATUS_SUCCESS",
        }.Select(l => parser.Feed(l, now)).ToList();
        Assert.All(shown, Assert.NotNull);
        Assert.Equal(new[] { "Documents · backup", "Documents · cleanup", "Photos · backup", "Photos · check" },
            shown.Where(l => l!.Kind == ConsoleLineKind.Section).Select(l => l!.Text));
        Assert.Equal("NAS", shown[7]!.Subtitle);
        Assert.Equal(now, shown[0]!.Time);
    }

    [Fact]
    public void Spans_set_apart_the_tag_the_percentage_and_paths()
    {
        var progress = "[progress] 12.3% · Backing up · 62,689 of ~422,166 files checked";
        var spans = ConsoleLineClassifier.Spans(progress, ConsoleLineKind.Progress);
        Assert.Contains(spans, s => s.Kind == ConsoleLineClassifier.SpanKind.Tag && progress.Substring(s.Start, s.Length) == "[progress]");
        Assert.Contains(spans, s => s.Kind == ConsoleLineClassifier.SpanKind.Percent && progress.Substring(s.Start, s.Length) == "12.3%");

        var file = "[file] added: C:\\Users\\sam\\My Documents\\Tax return 2025.pdf";
        var path = Assert.Single(ConsoleLineClassifier.Spans(file, ConsoleLineKind.File), s => s.Kind == ConsoleLineClassifier.SpanKind.Path);
        Assert.Equal("C:\\Users\\sam\\My Documents\\Tax return 2025.pdf", file.Substring(path.Start, path.Length));

        // A path ends where no Windows name can continue (":" after the drive).
        var setup = "[setup] Reading C:\\Users\\sam\\calls.xml: unsupported, then \\\\nas\\share\\photos.";
        var paths = ConsoleLineClassifier.Spans(setup, ConsoleLineKind.Setup).Where(s => s.Kind == ConsoleLineClassifier.SpanKind.Path)
            .Select(s => setup.Substring(s.Start, s.Length)).ToList();
        Assert.Equal(new[] { "C:\\Users\\sam\\calls.xml", "\\\\nas\\share\\photos" }, paths);

        // Backrest's own time and level are quiet with the tag.
        var record = "[tasklog] 20:05:02.000Z\terror\tbackup for plan \"p\"\ttask failed";
        var quiet = Assert.Single(ConsoleLineClassifier.Spans(record, ConsoleLineKind.Error));
        Assert.Equal("[tasklog] 20:05:02.000Z\terror", record.Substring(quiet.Start, quiet.Length));

        // Warnings and errors keep one colour; a header has no parts.
        Assert.DoesNotContain(ConsoleLineClassifier.Spans("[skip] C:\\Users\\sam", ConsoleLineKind.Warning), s => s.Kind == ConsoleLineClassifier.SpanKind.Path);
        Assert.Empty(ConsoleLineClassifier.Spans("local_c · backup", ConsoleLineKind.Section));
    }

    [Fact]
    public void An_error_list_entry_reads_without_the_tag_level_and_tabs()
    {
        Assert.Equal("attempt 1 failed: repository does not exist",
            ConsoleLineClassifier.Message("[orchestrator] attempt 1 failed: repository does not exist"));
        // Backrest's record: what failed and its error field, not the plan id.
        Assert.Equal("task failed: Fatal: unable to save snapshot: \"no space\"",
            ConsoleLineClassifier.Message("[tasklog] 06:01:02.000Z\terror\tbackup for plan \"p\"\ttask failed\t{\"error\": \"Fatal: unable to save snapshot: \\\"no space\\\"\"}"));
        Assert.Equal("backup for plan \"p\" · error processing item · {\"item\": \"C:\\x\"}",
            ConsoleLineClassifier.Message("[tasklog] 00:34:35.623Z\twarn\tbackup for plan \"p\"\terror processing item\t{\"item\": \"C:\\x\"}"));
        Assert.Equal("the destination drive is full", ConsoleLineClassifier.Message("last error    : the destination drive is full"));
        Assert.Equal("Fatal: unable to open repository", ConsoleLineClassifier.Message("Fatal: unable to open repository"));
        Assert.EndsWith("…", ConsoleLineClassifier.Message("[error] " + new string('x', 400)));
    }

    [Fact]
    public void Copy_text_carries_the_time_only_when_asked()
    {
        var time = new DateTime(2026, 11, 10, 20, 0, 3);
        var line = new ConsoleLine("[setup] Shadow copy on", ConsoleLineKind.Setup, time);
        Assert.Equal("[setup] Shadow copy on", line.CopyText(false));
        Assert.Equal("20:00:03  [setup] Shadow copy on", line.CopyText(true));
        var section = new ConsoleLine("local_c · backup", ConsoleLineKind.Section, time) { Subtitle = "Google Drive" };
        Assert.Equal("── local_c · backup · 20:00 · Google Drive", section.CopyText(false));
        Assert.Equal("[orchestrator] x", new ConsoleLine("[orchestrator] x", ConsoleLineKind.Plain).CopyText(true));
    }

    [Fact]
    public void The_shown_list_changes_only_where_the_lines_did()
    {
        var a = Enumerable.Range(0, 6).Select(i => new ConsoleLine("line " + i, ConsoleLineKind.Plain)).ToList();
        var target = new AvaloniaList<ConsoleLine>(a.Take(4));
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, e) => events.Add(e.Action);

        LogsViewModel.ApplyLines(target, a); // appended
        Assert.Equal(a, target);
        Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Add }, events);

        events.Clear();
        LogsViewModel.ApplyLines(target, a.Skip(2).ToList()); // gone from the top
        Assert.Equal(a.Skip(2), target);
        Assert.Equal(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Remove }, events);

        events.Clear();
        var replaced = a.Skip(2).Take(2).Append(new ConsoleLine("new end", ConsoleLineKind.Plain)).ToList();
        LogsViewModel.ApplyLines(target, replaced); // the end replaced
        Assert.Equal(replaced, target);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, events);
        Assert.Same(a[2], target[0]);

        LogsViewModel.ApplyLines(target, Array.Empty<ConsoleLine>());
        Assert.Empty(target);
    }
}
