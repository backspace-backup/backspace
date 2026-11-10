using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Backspace.Models;
using Backspace.Services;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios;

/// <summary>
/// U176 (10 November 2026): the Activity &amp; logs console colours lines by
/// type, has a remembered Timestamps column, draws a header where a run, a
/// check or a cleanup starts, copies a run or a part of it, opens the log
/// file, lists a failed run's errors (each jumps to its line), and stays
/// smooth with a 100,000-line log. Drives the real page through its
/// controls, with run logs written in the format the orchestrator writes.
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class S96_LogConsole : ScenarioBase
{
    protected override string ScenarioName => "S96-log-console";

    [AvaloniaFact] public async Task Run_S96() => await Run();

    private static readonly DateTime Evening = DateTime.Today.AddHours(20).ToUniversalTime();

    /// <summary>A run log as BackupOrchestrator writes it: the header block,
    /// stamped engine lines, the outcome block.</summary>
    private static string RunLog(string backup, string destination, DateTime startUtc, IEnumerable<(int Second, string Line)> lines,
        string status, string summary, string? lastError = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- Backspace cell log ---");
        sb.AppendLine($"backup        : {backup}");
        sb.AppendLine(@"source        : C:\");
        sb.AppendLine($"destination   : {destination} (LocalFolder)");
        sb.AppendLine("storage-name  : target-4b7b177d");
        sb.AppendLine($"started (UTC) : {startUtc:o}");
        sb.AppendLine();
        foreach (var (second, line) in lines) sb.AppendLine(LogLineStamp.Stamp(line, startUtc.AddSeconds(second)));
        sb.AppendLine();
        sb.AppendLine("--- cell outcome ---");
        sb.AppendLine($"status        : {status}");
        sb.AppendLine("attempts      : 1");
        sb.AppendLine($"summary       : {summary}");
        if (lastError is not null) sb.AppendLine($"last error    : {lastError}");
        sb.AppendLine($"ended (UTC)   : {startUtc.AddSeconds(640):o}");
        return sb.ToString();
    }

    private static IEnumerable<(int, string)> SuccessfulLines(string dest)
    {
        yield return (0, $"[section] local_c · backup\t{dest}");
        yield return (0, $"[orchestrator] attempt 1 starting at {Evening:o}");
        yield return (1, $"[setup] Previous backup to «{dest}»: 2026-11-08 20:00, snapshot 7ee22f0f. Only changes are sent.");
        yield return (1, "[setup] 259 exclusion rules loaded; files kept only in the cloud are skipped.");
        yield return (1, "[setup] Shadow copy on: open and locked files are read from a snapshot of the drive.");
        yield return (2, "[backrest] op=77 status=STATUS_INPROGRESS");
        yield return (3, "[repo-manager] 20:00:03.582Z\tdebug\trepo orchestrator starting backup\t{\"repo\": \"952aab70-target-952aab70\"}");
        yield return (3, "[restic] 20:00:03.582Z\tdebug\tcommand: \"restic.exe backup --json C:\\ --exclude-caches\"");
        var files = 0;
        for (var i = 0; i < 24; i++)
        {
            var pct = Math.Min(99.0, i * 4.3);
            files += 15_000 + i * 97;
            yield return (5 + i * 24, FormattableString.Invariant(
                $"[progress] {pct:0.0}% · Backing up · {files:N0} of ~422,166 files checked (~176 GB) · {i * 81} MB of ~17 GB changed files backed up · {i * 47} MB of new data uploaded, 1.4 MB/s · about {Math.Max(1, 9 - i / 3)} min left"));
            if (i == 9)
            {
                yield return (230, "[restic] 20:04:46.637Z\tdebug\trclone: 2026/11/09 20:04:46 ERROR : data/ba/ba2a2566: Post request put error: googleapi: Error 500");
                yield return (234, "[restic] 20:04:50.212Z\tdebug\tSave(<data/ba2a256626>) returned error, retrying after 962ms: unexpected HTTP response (500)");
            }
        }
        yield return (578, "[file] added: C:\\Users\\sam\\Documents\\Taxes\\2025 return.pdf");
        yield return (578, "[file] changed: C:\\Users\\sam\\AppData\\Roaming\\Code\\User\\settings.json");
        yield return (579, "[tasklog] 20:10:03.139Z\tinfo\tbackup for plan \"local_c-target\"\tbackup complete\t{\"plan\": \"local_c-target\"}");
        yield return (579, "[recap] Backup complete: 375,523 files (175 GB) checked · 26,969 new, 3,212 changed, 345,342 unchanged · 1.8 GB added to the storage · took 9m 39s · snapshot 4bd48ee6");
        yield return (580, "[backspace] 1 online-only file skipped: kept only in the cloud, not downloaded and not in this backup");
        yield return (580, "[backspace] Finishing snapshot history");
        yield return (590, $"[section] local_c · cleanup\t{dest}");
        yield return (590, "[backrest] op=80 status=STATUS_INPROGRESS");
        yield return (600, "[backrest] op=80 status=STATUS_SUCCESS");
        yield return (600, "[cleanup] Nothing to remove: the keep rules keep every backup.");
        yield return (601, $"[section] local_c · check\t{dest}");
        yield return (601, "[backspace] Data check targets 524,288,000 bytes per repository; whole-pack sampling, metadata and retries can change actual traffic.");
        yield return (630, "[backrest] op=81 status=STATUS_SUCCESS");
    }

    private static IEnumerable<(int, string)> FailedLines(string dest)
    {
        yield return (0, $"[section] local_c · backup\t{dest}");
        yield return (0, $"[orchestrator] attempt 1 starting at {Evening:o}");
        yield return (1, $"[setup] Previous backup to «{dest}»: 2026-11-08 20:00, snapshot 7ee22f0f. Only changes are sent.");
        yield return (1, "[setup] Shadow copy on: open and locked files are read from a snapshot of the drive.");
        for (var i = 0; i < 30; i++)
            yield return (5 + i * 10, FormattableString.Invariant($"[progress] {i * 1.7:0.0}% · Backing up · {i * 4_100:N0} of ~422,166 files checked (~176 GB)"));
        yield return (300, "[restic] {\"item\":\"C:\\\\Users\\\\sam\\\\Apps\\\\Phone\\\\calls.xml\",\"during\":\"archival\",\"message\":\"C:\\\\Users\\\\sam\\\\Apps\\\\Phone\\\\calls.xml: unsupported file type \\\"irregular\\\"\"}");
        yield return (301, "[tasklog] 20:05:01.000Z\twarn\tbackup for plan \"local_c-target\"\terror processing item\t{\"item\": \"C:\\\\Users\\\\sam\\\\Apps\\\\Phone\\\\sms.xml\"}");
        yield return (302, "[tasklog] 20:05:02.000Z\terror\tbackup for plan \"local_c-target\"\ttask failed\t{\"error\": \"Fatal: unable to save snapshot: no space left on device\"}");
        yield return (302, "[backrest] op=4 status=STATUS_ERROR Fatal: unable to save snapshot: no space left on device");
        yield return (303, "[orchestrator] attempt 1 failed: Backup result: STATUS_ERROR. Fatal: unable to save snapshot: no space left on device");
        yield return (303, "[orchestrator] non-retriable failure: the destination drive is full");
        for (var i = 0; i < 25; i++)
            yield return (304, "[restic] 20:05:04.000Z\tdebug\tunlock: lock " + i + " released");
    }

    private static IEnumerable<(int, string)> WarningLines(string dest)
    {
        yield return (0, $"[section] local_c · backup\t{dest}");
        yield return (0, $"[orchestrator] attempt 1 starting at {Evening:o}");
        yield return (0, "[orchestrator] Files that other apps had open may have been skipped, because your latest changes weren't applied to the background service yet. Open Settings → Administrator access and choose Try again to include them.");
        yield return (1, "[setup] No earlier backup of this folder in «USB drive (E:)»: everything is read and sent.");
        yield return (1, "[setup] Shadow copy off: files another program keeps locked may be skipped.");
        yield return (6, "[backrest] op=4 status=STATUS_WARNING Partial backup, some files may not have been read completely.");
        foreach (var name in new[] { "calls.xml", "calls.xsl", "sms.xml", "sms.xsl" })
            yield return (6, "[restic] {\"item\":\"C:\\\\Users\\\\sam\\\\Apps\\\\Phone\\\\" + name + "\",\"during\":\"archival\",\"message\":\"C:\\\\Users\\\\sam\\\\Apps\\\\Phone\\\\" + name + ": unsupported file type \\\"irregular\\\"\"}");
        yield return (7, "[recap] Backup complete: 1 files (178 B) checked · 1 new, 0 changed, 0 unchanged · 7.6 KB added to the storage · took 0m 00s · snapshot 446aecc3");
        yield return (8, "[backspace] Finishing snapshot history");
    }

    private static void Click(Window host, Control target)
    {
        Assert.True(target.IsEffectivelyVisible && target.IsEffectivelyEnabled, $"{target.Name ?? target.GetType().Name} can't be clicked");
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), host)!.Value;
        host.MouseDown(at, MouseButton.Left); host.MouseUp(at, MouseButton.Left);
    }

    private static Point Center(Window host, Control target) =>
        target.TranslatePoint(new Point(Math.Min(60, target.Bounds.Width / 2), target.Bounds.Height / 2), host)!.Value;

    private static IBrush DarkBrush(string key) =>
        Application.Current!.TryGetResource(key, ThemeVariant.Dark, out var value) ? (IBrush)value! : throw new InvalidOperationException(key);

    private static void SetTheme(ThemeVariant variant)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = variant;
    }

    protected override async Task RunAsync(ScenarioContext ctx)
    {
        var dest = new Destination { Name = "USB drive (E:)", Kind = DestinationKind.LocalFolder, PathOrSubpath = ctx.MakeLocalDestination("usb") };
        var backup = new Backup { Name = "local_c", SourcePaths = { ctx.MakeSource("c", seed: 3, targetBytes: 50_000) } };
        backup.Targets.Add(new BackupTarget { DestinationId = dest.Id, StorageName = "default" });
        var photos = new Backup { Name = "Photos" };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest); cfg.Backups.Add(backup); cfg.Backups.Add(photos);
            cfg.Logging.MinimumLevel = "Information"; cfg.Logging.ShowTimestamps = false;
        });

        var logDir = Path.Combine(ctx.RootDir, "run-logs");
        Directory.CreateDirectory(logDir);
        RunRecord Save(string name, DateTime startUtc, BackupRunStatus status, string summary, string text)
        {
            var path = Path.Combine(logDir, name + ".log");
            File.WriteAllText(path, text);
            var record = new RunRecord
            {
                BackupId = backup.Id, BackupName = backup.Name, StartedUtc = startUtc, EndedUtc = startUtc.AddMinutes(10),
                Status = status, Summary = summary, LogPath = path,
            };
            ServiceLocator.Logs.AppendRun(record);
            return record;
        }
        var big = new StringBuilder();
        big.AppendLine(LogSection.Line("local_c", "backup", dest.Name));
        for (var i = 0; i < 100_000; i++)
            big.Append("[progress] ").Append((i / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append("% · ").Append(i).AppendLine(" files");
        var bigRun = Save("big", Evening.AddDays(-3), BackupRunStatus.Success, "100,000 lines", big.ToString());
        var warned = Save("warned", Evening.AddDays(-2), BackupRunStatus.Warning, "Partial backup",
            RunLog("local_c", dest.Name, Evening.AddDays(-2), WarningLines(dest.Name), "Warning", "Backup result: STATUS_WARNING. Partial backup, some files may not have been read completely."));
        var failed = Save("failed", Evening.AddDays(-1), BackupRunStatus.Failed, "The destination drive is full",
            RunLog("local_c", dest.Name, Evening.AddDays(-1), FailedLines(dest.Name), "Failed", "Backup result: STATUS_ERROR.", "the destination drive is full"));
        var success = Save("success", Evening, BackupRunStatus.Success, "1.83 GB uploaded",
            RunLog("local_c", dest.Name, Evening, SuccessfulLines(dest.Name), "Success", "Backup result: STATUS_SUCCESS."));

        var copied = new List<string>();
        var opened = new List<string>();
        LogsViewModel.ClipboardOverride = copied.Add;
        FolderLauncher.LaunchOverride = opened.Add;
        try
        {
            using var main = new MainWindowViewModel();
            var host = ctx.Driver.Show(new MainWindow { Width = 1180, Height = 760, DataContext = main });
            main.SelectedNav = NavItem.Logs;
            await ctx.Driver.PumpAsync(200);
            var vm = main.Logs;
            var view = host.GetVisualDescendants().OfType<LogsView>().Single();
            view.FindControl<ComboBox>("BackupPicker")!.SelectedItem = backup.Name;
            await ctx.Driver.PumpAsync(200);
            var scroll = view.FindControl<ScrollViewer>("LogScroll")!;
            var list = view.FindControl<ItemsControl>("LogLines")!;
            List<ConsoleLineRow> Rows() => list.GetVisualDescendants().OfType<ConsoleLineRow>().Where(r => r.IsEffectivelyVisible && r.Line is not null).ToList();
            ConsoleLineRow Row(Func<ConsoleLine, bool> match) => Rows().First(r => match(r.Line!));
            async Task Open(RunRecord run, string mustShow)
            {
                vm.SelectedRun = vm.History.Single(r => r.Id == run.Id);
                Assert.True(ctx.Driver.WaitFor(() => vm.DisplayText.Contains(mustShow), 8000), "The run's log did not load: " + run.Summary);
                await ctx.Driver.PumpAsync(150);
            }

            // ---- A finished run, coloured by line type ----
            await Open(success, "[recap]");
            Assert.True(view.IsFollowing);
            Assert.True(Math.Abs(scroll.Offset.Y - Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)) < 2, "A newly opened run should show its last line.");
            ctx.Snapshot(host, "finished run end",
                "A finished backup, opened at its end (light app theme): the cleanup and check sections each start with a thin rule and a bold header 'local_c · cleanup · 20:09' / 'local_c · check · 20:10' with the destination dim at the right and a Copy button; the recap and 'online-only file skipped' in the accent blue; the cell outcome block dim with its status in blue.");
            Assert.Equal(new[] { "local_c · backup", "local_c · cleanup", "local_c · check" },
                vm.DisplayLines.Where(l => l.Kind == ConsoleLineKind.Section).Select(l => l.Text));
            Assert.DoesNotContain(vm.DisplayLines, l => l.IsDetail); // Verbose is off
            // Colours come from the theme's dark-variant status tokens.
            Assert.Equal(DarkBrush("BS.Brush.AccentTintFg"), Row(l => l.Text.StartsWith("[recap]")).TextBlock.Foreground);
            Assert.Equal(DarkBrush("BS.Brush.AccentTintFg"), Row(l => l.Text.Contains("online-only")).TextBlock.Foreground);
            Assert.Equal(DarkBrush("BS.Brush.FgDim"), Row(l => l.Text.StartsWith("[backrest] op=80 status=STATUS_SUCCESS")).TextBlock.Foreground);

            scroll.Offset = new Vector(0, 0);
            await ctx.Driver.PumpAsync(150);
            Assert.False(view.IsFollowing);
            var header = Row(l => l.Kind == ConsoleLineKind.Section);
            Assert.Matches(@"^local_c · backup · \d\d:\d\d$", header.Line!.SectionTitle);
            Assert.Equal(@"C:\ → USB drive (E:)", header.Line.Subtitle);
            // The first header needs no rule above it; the header fields show with Verbose only.
            Assert.False(header.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("ln-rule")).IsVisible);
            Assert.DoesNotContain(Rows(), r => r.Line!.Kind == ConsoleLineKind.Meta && r.Line.Text.StartsWith("storage-name"));
            Assert.Equal(DarkBrush("BS.Brush.FgDim"), Row(l => l.Text.StartsWith("[setup]")).TextBlock.Foreground);
            var progressRow = Row(l => l.Kind == ConsoleLineKind.Progress);
            Assert.Equal(DarkBrush("BS.Brush.FgSecondary"), progressRow.TextBlock.Foreground);
            var pct = progressRow.TextBlock.Inlines!.OfType<Run>().Single(r => r.FontWeight == FontWeight.SemiBold);
            Assert.EndsWith("%", pct.Text);
            Assert.Equal(DarkBrush("BS.Brush.FgPrimary"), pct.Foreground);
            Assert.False(Row(l => l.Kind == ConsoleLineKind.Setup).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("ln-time")).IsEffectivelyVisible);
            ctx.Snapshot(host, "finished run start",
                "The same run scrolled to the top: 'local_c · backup · 20:00' header with 'USB drive (E:)' on the right; the file's header fields and the setup lines dim; progress lines muted grey with each percentage in bright semibold; the [tag] of each line quieter than its message; paths brighter.");

            // ---- Timestamps: a narrow dim column, remembered ----
            var stamps = view.FindControl<CheckBox>("TimestampsLog")!;
            Assert.False(stamps.IsChecked);
            Click(host, stamps);
            await ctx.Driver.PumpAsync(150);
            Assert.True(vm.ShowTimestamps);
            var reopened = new ConfigStore(); reopened.Load();
            Assert.True(reopened.Current.Logging.ShowTimestamps, "Timestamps was not saved in the settings.");
            var setupRow = Row(l => l.Text.StartsWith("[setup] Shadow copy on"));
            var time = setupRow.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("ln-time"));
            Assert.True(time.IsEffectivelyVisible);
            Assert.Equal(Evening.AddSeconds(1).ToLocalTime().ToString("HH:mm:ss"), time.Text);
            Assert.True(time.Bounds.Width <= 70, "The time column is not narrow.");
            // A header carries its time in its title: its column stays empty, and it aligns with the lines.
            var head = Row(l => l.Kind == ConsoleLineKind.Section);
            Assert.Equal("", head.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("ln-time")).Text);
            Assert.Equal(setupRow.TextBlock.TranslatePoint(new Point(), list)!.Value.X, head.TextBlock.TranslatePoint(new Point(), list)!.Value.X, 0.5);
            ctx.Snapshot(host, "timestamps on",
                "Timestamps ticked: each line's local time (HH:mm:ss) in a narrow dim column on the left; the header's fields, which have no time, leave it empty and stay aligned.");

            // ---- Copy: the whole log, one section, selected lines ----
            Click(host, view.FindControl<Button>("CopyLog")!);
            await ctx.Driver.PumpAsync(50);
            var all = Assert.Single(copied);
            Assert.Contains("── local_c · cleanup · ", all);
            Assert.Contains(Evening.AddSeconds(1).ToLocalTime().ToString("HH:mm:ss") + "  [setup] Shadow copy on", all);
            Assert.DoesNotContain("\tdebug\t", all);
            copied.Clear();
            scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            await ctx.Driver.PumpAsync(150);
            var cleanupHeader = Row(l => l.Kind == ConsoleLineKind.Section && l.Text == "local_c · cleanup");
            Click(host, cleanupHeader.GetVisualDescendants().OfType<Button>().Single());
            await ctx.Driver.PumpAsync(50);
            var section = Assert.Single(copied);
            Assert.StartsWith("── local_c · cleanup", section); // a header carries its time in its title
            Assert.Contains("[cleanup] Nothing to remove", section);
            Assert.DoesNotContain("[recap]", section);
            Assert.DoesNotContain("local_c · check", section);
            copied.Clear();
            // Drag over two lines, then Ctrl+C.
            var first = Row(l => l.Text.StartsWith("[recap]"));
            var last = Row(l => l.Text.StartsWith("[backspace] Finishing"));
            host.MouseDown(Center(host, first), MouseButton.Left);
            host.MouseMove(Center(host, last));
            host.MouseUp(Center(host, last), MouseButton.Left);
            await ctx.Driver.PumpAsync(50);
            Assert.True(first.Classes.Contains("selected") && last.Classes.Contains("selected"));
            scroll.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.C, KeyModifiers = KeyModifiers.Control });
            await ctx.Driver.PumpAsync(50);
            var lines = Assert.Single(copied).Split('\n');
            Assert.Equal(3, lines.Length);
            Assert.EndsWith("[recap] Backup complete: 375,523 files (175 GB) checked · 26,969 new, 3,212 changed, 345,342 unchanged · 1.8 GB added to the storage · took 9m 39s · snapshot 4bd48ee6", lines[0]);
            Assert.EndsWith("[backspace] Finishing snapshot history", lines[2]);
            ctx.Snapshot(host, "lines selected",
                "Three lines selected by dragging from the recap to 'Finishing snapshot history' (blue selection); Ctrl+C copied exactly those, with their times.");
            copied.Clear();
            // Dragging past the bottom edge scrolls on and keeps selecting; Escape clears.
            scroll.Offset = new Vector(0, 0);
            await ctx.Driver.PumpAsync(100);
            var shownRows = Rows().Count(r => r.TranslatePoint(new Point(), scroll)!.Value.Y is >= 0 and var top && top < scroll.Viewport.Height);
            var start = Rows().First(r => r.Line!.Text.StartsWith("[setup] Previous"));
            var startLine = start.Line!; // the row is recycled once it scrolls away
            host.MouseDown(Center(host, start), MouseButton.Left);
            var below = scroll.TranslatePoint(new Point(60, scroll.Viewport.Height + 60), host)!.Value;
            for (var i = 0; i < 8; i++) { host.MouseMove(below); await ctx.Driver.PumpAsync(20); }
            host.MouseUp(below, MouseButton.Left);
            await ctx.Driver.PumpAsync(50);
            Assert.True(scroll.Offset.Y > 0, "Dragging below the log did not scroll it.");
            var selectedCount = vm.DisplayLines.Count(l => l.IsSelected);
            Assert.True(selectedCount > shownRows, $"{selectedCount} lines selected; the drag should reach past the {shownRows} first shown.");
            Assert.True(startLine.IsSelected);
            ctx.Snapshot(host, "drag past the edge",
                "Dragging from '[setup] Previous backup…' to below the console scrolled it on: every line from there to the bottom of the view is selected.");
            scroll.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.DoesNotContain(vm.DisplayLines, l => l.IsSelected);


            // ---- Open log file ----
            var openFile = view.FindControl<Button>("OpenLogFile")!;
            Click(host, openFile);
            Assert.Equal(new[] { success.LogPath }, opened);

            // ---- Verbose shows the engine detail with its own colour ----
            Click(host, view.FindControl<CheckBox>("VerboseLog")!);
            Assert.True(ctx.Driver.WaitFor(() => vm.DisplayLines.Any(l => l.IsDetail)));
            await ctx.Driver.PumpAsync(150);
            scroll.Offset = new Vector(0, 0);
            await ctx.Driver.PumpAsync(150);
            Assert.Equal(DarkBrush("BS.Brush.FgDim"), Row(l => l.Kind == ConsoleLineKind.Detail).TextBlock.Foreground);
            ctx.Snapshot(host, "verbose on",
                "Verbose ticked: Backrest's state polls (STATUS_INPROGRESS) and debug records appear, dim, between the setup and progress lines.");
            Click(host, view.FindControl<CheckBox>("VerboseLog")!);
            Assert.True(ctx.Driver.WaitFor(() => !vm.DisplayLines.Any(l => l.IsDetail)));

            // ---- A failed run: its errors listed, each jumps to its line ----
            await Open(failed, "non-retriable failure");
            var errorList = view.FindControl<Border>("ErrorList")!;
            Assert.True(errorList.IsEffectivelyVisible);
            Assert.Equal(5, vm.ErrorLines.Count); // tasklog error, STATUS_ERROR, attempt failed, non-retriable, last error
            Assert.Equal("5 errors in this run", view.FindControl<TextBlock>("ErrorListTitle")!.Text);
            Assert.Equal(DarkBrush("BS.Brush.Warn"), Row(l => l.Text.StartsWith("[tasklog]") && l.Kind == ConsoleLineKind.Warning).TextBlock.Foreground);
            Assert.Equal(DarkBrush("BS.Brush.DangerTintFg"), Row(l => l.Text.StartsWith("[orchestrator] attempt 1 failed")).TextBlock.Foreground);
            ctx.Snapshot(host, "failed run errors",
                "A failed run: above its log, '5 errors in this run' lists each error (time and message, red); the warnings in the log are amber and the errors red.");
            scroll.Offset = new Vector(0, 0);
            await ctx.Driver.PumpAsync(100);
            var target = vm.ErrorLines[0];
            var jumps = view.FindControl<ItemsControl>("ErrorItems")!.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(5, jumps.Count);
            Click(host, jumps[0]);
            await ctx.Driver.PumpAsync(100);
            Assert.True(target.IsFlashing);
            var jumped = Row(l => ReferenceEquals(l, target));
            var y = jumped.TranslatePoint(new Point(), scroll)!.Value.Y;
            Assert.InRange(y, 0, scroll.Viewport.Height - jumped.Bounds.Height);
            Assert.True(jumped.Classes.Contains("flash"));
            // Following resumes only when the line shown is in the last screen of the log.
            var atEnd = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 4;
            Assert.Equal(atEnd, view.IsFollowing);
            Assert.Equal(!atEnd, view.FindControl<Button>("JumpToLatest")!.IsVisible);
            ctx.Snapshot(host, "error jump",
                "After clicking the first error in the list: the log scrolled to the [tasklog] 'task failed' line, a third of the way down, highlighted in a red tint for a moment; Jump to latest is offered.");
            // A run that did not fail has no list, even with warnings.
            await Open(warned, "[recap]");
            Assert.False(errorList.IsEffectivelyVisible);
            Assert.All(Rows().Where(r => r.Line!.Text.StartsWith("[restic] {")), r => Assert.Equal(DarkBrush("BS.Brush.Warn"), r.TextBlock.Foreground));
            ctx.Snapshot(host, "warning run",
                "A run with warnings: restic's unreadable files and Backrest's STATUS_WARNING in amber, the outcome 'Warning' amber, no error list; the recap below them in the accent blue, semibold.");

            // The recap stands out by weight as well as colour, and no accent's
            // console tint is the warning or error colour (Terracotta's is
            // orange, Graphite's near-white).
            var recapRow = Row(l => l.Text.StartsWith("[recap]"));
            Assert.Equal(FontWeight.SemiBold, recapRow.TextBlock.FontWeight);
            Assert.Equal(FontWeight.Normal, Row(l => l.Kind == ConsoleLineKind.Warning).TextBlock.FontWeight);
            var warn = ((ISolidColorBrush)DarkBrush("BS.Brush.Warn")).Color;
            var danger = ((ISolidColorBrush)DarkBrush("BS.Brush.DangerTintFg")).Color;
            foreach (var accent in Enum.GetNames<AccentColor>())
            {
                var dictionary = (IResourceProvider)Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(new Uri($"avares://Backspace/Themes/Accent_{accent}.axaml"));
                Assert.True(dictionary.TryGetResource("BS.Brush.AccentTintFg", ThemeVariant.Dark, out var tint), accent);
                var color = ((ISolidColorBrush)tint!).Color;
                Assert.NotEqual(warn, color);
                Assert.NotEqual(danger, color);
            }

            // ---- Live: several runs in a row, each with its header ----
            vm.SelectedRun = null;
            await ctx.Driver.PumpAsync(100);
            Assert.True(vm.IsShowingLive);
            Assert.False(view.FindControl<Button>("OpenLogFile")!.IsEffectivelyVisible);
            Click(host, view.FindControl<Button>("ClearLog")!);
            host.MouseMove(new Point(2, 2)); // no tooltip over the screenshots
            await ctx.Driver.PumpAsync(50);
            var engine = ServiceLocator.Engine;
            foreach (var (_, line) in WarningLines("USB drive (E:)")) engine.RaiseLineWritten(line);
            engine.RaiseLineWritten("[section] local_c · cleanup\tUSB drive (E:)");
            engine.RaiseLineWritten("[cleanup] 2 old backups removed · 1.4 GB freed in the storage");
            engine.RaiseLineWritten("[section] Photos · backup\tNAS");
            engine.RaiseLineWritten("[setup] Previous backup to «NAS»: 2026-11-09 20:00, snapshot 1a2b3c4d. Only changes are sent.");
            for (var i = 0; i < 8; i++)
                engine.RaiseLineWritten(FormattableString.Invariant($"[progress] {i * 4.5:0.0}% · Backing up · {i * 1_250:N0} of ~28,000 files checked (~84 GB)"));
            Assert.True(ctx.Driver.WaitFor(() => vm.DisplayLines.Count(l => l.Kind == ConsoleLineKind.Section) == 3, 4000));
            await ctx.Driver.PumpAsync(200);
            Assert.True(view.IsFollowing);
            Assert.True(Math.Abs(scroll.Offset.Y - Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)) < 2, "The live view did not follow its last line.");
            Assert.All(vm.DisplayLines.Where(l => l.Kind != ConsoleLineKind.Section), l => Assert.NotNull(l.Time));
            ctx.Snapshot(host, "live several runs",
                "The live view after a backup of local_c, its cleanup and a backup of Photos started: three headers ('local_c · backup', 'local_c · cleanup', 'Photos · backup') keep them apart; the Photos backup is still running, its progress lines arriving at the end with their arrival times.");

            // ---- Dark app theme: the console reads the same ----
            SetTheme(ThemeVariant.Dark);
            try
            {
                await ctx.Driver.PumpAsync(200);
                ctx.Snapshot(host, "live several runs dark",
                    "The same live view in the dark app theme: the console's colours are unchanged, the page around it dark.");
                await Open(failed, "non-retriable failure");
                ctx.Snapshot(host, "failed run dark", "The failed run with its error list in the dark app theme.");
            }
            finally { SetTheme(ThemeVariant.Light); }
            vm.SelectedRun = null;
            await ctx.Driver.PumpAsync(100);

            // A restore writes no marker of its own: the page starts its header.
            var restoring = new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Kind = RunKind.Restore,
                StartedUtc = DateTime.UtcNow, Status = BackupRunStatus.Running, Summary = "Running…" };
            ServiceLocator.Activity.Register(restoring);
            try
            {
                AppNavigator.SignalLiveActivity(backup.Name);
                await ctx.Driver.PumpAsync(200);
                Assert.True(vm.IsShowingLive);
                engine.RaiseLineWritten("[restore] starting 3 item(s)");
                engine.RaiseLineWritten("[restore] done: status=Success 3/3");
                Assert.True(ctx.Driver.WaitFor(() => vm.DisplayText.Contains("[restore] done"), 4000));
                var restoreHeader = vm.DisplayLines.First();
                Assert.Equal(ConsoleLineKind.Section, restoreHeader.Kind);
                Assert.Equal("local_c · restore", restoreHeader.Text);
                Assert.Equal(ConsoleLineKind.Summary, vm.DisplayLines.Last().Kind);
                await ctx.Driver.PumpAsync(100);
                ctx.Snapshot(host, "live restore",
                    "A restore started from the Restore page: the live view starts again with a 'local_c · restore · HH:mm' header; 'done: status=Success 3/3' in the accent.");
            }
            finally { ServiceLocator.Activity.Unregister(restoring.Id); }
            // The finished restore leaves the run picker on a saved run; back to live output.
            await ctx.Driver.PumpAsync(200);
            vm.SelectedRun = null;
            await ctx.Driver.PumpAsync(100);

            // The live view keeps the last 80,000 of more than 100,000 lines, says so first, and still follows.
            Click(host, view.FindControl<Button>("ClearLog")!);
            host.MouseMove(new Point(2, 2));
            for (var i = 0; i <= LogsViewModel.LiveLineCap; i++) engine.RaiseLineWritten("[progress] 1.0% · " + i);
            Assert.True(ctx.Driver.WaitFor(() => vm.DisplayLines.Count is > 1000 and < LogsViewModel.LiveLineCap, 20_000), "The live view did not trim to its cap.");
            await ctx.Driver.PumpAsync(200);
            Assert.StartsWith("[live-tail truncated", vm.DisplayLines[0].Text);
            Assert.EndsWith("· " + LogsViewModel.LiveLineCap, vm.DisplayLines[^1].Text);
            Assert.True(view.IsFollowing);
            Assert.True(Math.Abs(scroll.Offset.Y - Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)) < 2, "The long live view did not follow its last line.");
            Assert.True(Rows().Count < 200, "The live view is not virtualized.");
            Click(host, view.FindControl<Button>("ClearLog")!);
            await ctx.Driver.PumpAsync(50);
            Assert.Empty(vm.DisplayLines);


            // ---- 100,000 lines: virtualized, smooth ----
            vm.ShowTimestamps = false;
            var watch = Stopwatch.StartNew();
            vm.SelectedRun = vm.History.Single(r => r.Id == bigRun.Id);
            Assert.True(ctx.Driver.WaitFor(() => vm.DisplayLines.Count > 100_000, 20_000), "The 100,000-line log did not load.");
            await ctx.Driver.PumpAsync(50);
            var loaded = watch.Elapsed;
            Assert.True(Rows().Count < 200, $"{Rows().Count} rows drawn for a 100,000-line log: the list is not virtualized.");
            Assert.True(Math.Abs(scroll.Offset.Y - Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)) < 2, "The long log did not open at its end.");
            var jumpsTimed = Stopwatch.StartNew();
            foreach (var fraction in new[] { 0.0, 0.5, 0.25, 0.9 })
            {
                scroll.Offset = new Vector(0, fraction * (scroll.Extent.Height - scroll.Viewport.Height));
                scroll.UpdateLayout();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
            var scrolled = jumpsTimed.Elapsed;
            Assert.True(scrolled < TimeSpan.FromSeconds(2), $"Four jumps across 100,000 lines took {scrolled.TotalMilliseconds:N0} ms.");
            Assert.True(Rows().Count < 200);
            Assert.Contains(Rows(), r => r.Line!.Kind == ConsoleLineKind.Progress);
            ctx.Report.RecordNote($"100,000-line log: loaded and shown in {loaded.TotalMilliseconds:N0} ms; four scroll jumps in {scrolled.TotalMilliseconds:N0} ms; {Rows().Count} rows drawn.");
            ctx.Snapshot(host, "100k lines",
                $"A 100,000-line log scrolled to 90%: only the visible rows are drawn ({Rows().Count}); it loaded in {loaded.TotalMilliseconds:N0} ms and four jumps across it took {scrolled.TotalMilliseconds:N0} ms.");
        }
        finally
        {
            LogsViewModel.ClipboardOverride = null;
            FolderLauncher.LaunchOverride = null;
        }
    }
}
