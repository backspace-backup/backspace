using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U175 step 0: the runner that starts restic itself. Recorded restic output
/// for each message type, cut and unfinished lines, every exit code, the
/// password kept off the command line, and stopping: gently (restic removes
/// its own lock), or by ending the whole tree when the program does not stop,
/// rclone included.
/// </summary>
public sealed class ResticRunnerTests(ITestOutputHelper output)
{
    private static ResticRepository Repository(string password = "correct horse battery staple", params string[] flags) =>
        new("C:\\repos\\a b", password, new[] { "RCLONE_CONFIG=C:\\cfg\\remote.conf", "AWS_ACCESS_KEY_ID=key-id" }, flags);

    [Fact]
    public void The_password_goes_only_in_the_environment_and_inherited_overrides_are_removed()
    {
        var inherited = new[] { "RESTIC_PASSWORD", "RESTIC_PASSWORD_FILE", "RESTIC_PASSWORD_COMMAND", "RESTIC_REPOSITORY_FILE", "RESTIC_PROGRESS_FPS" };
        var saved = inherited.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("RESTIC_PASSWORD", "an inherited password");
            Environment.SetEnvironmentVariable("RESTIC_PASSWORD_FILE", @"C:\elsewhere\password.txt");
            Environment.SetEnvironmentVariable("RESTIC_PASSWORD_COMMAND", "type secret.txt");
            Environment.SetEnvironmentVariable("RESTIC_REPOSITORY_FILE", @"C:\elsewhere\repository.txt");
            Environment.SetEnvironmentVariable("RESTIC_PROGRESS_FPS", "30");
            var repository = Repository("correct horse battery staple", "--limit-upload", "100", "--limit-download", "100");
            var start = ResticRunner.StartInfo(new ResticCommand("C:\\engines\\restic.exe", repository, ["backup", "--json", "D:\\"],
                ["--exclude-caches", "--tag", "plan:p"]) { ProgressFps = 2 });
            Assert.Equal(new[] { "backup", "--json", "D:\\", "--limit-upload", "100", "--limit-download", "100", "-o", "sftp.args=-oBatchMode=yes",
                "--exclude-caches", "--tag", "plan:p" }, start.ArgumentList);
            Assert.DoesNotContain(start.ArgumentList, a => a.Contains("horse", StringComparison.Ordinal));
            Assert.Equal("correct horse battery staple", start.Environment["RESTIC_PASSWORD"]);
            Assert.Equal("C:\\repos\\a b", start.Environment["RESTIC_REPOSITORY"]);
            Assert.Equal("C:\\cfg\\remote.conf", start.Environment["RCLONE_CONFIG"]);
            Assert.Equal("key-id", start.Environment["AWS_ACCESS_KEY_ID"]);
            Assert.Equal("2", start.Environment["RESTIC_PROGRESS_FPS"]);
            Assert.False(start.Environment.ContainsKey("RESTIC_PASSWORD_FILE"));
            Assert.False(start.Environment.ContainsKey("RESTIC_PASSWORD_COMMAND"));
            Assert.False(start.Environment.ContainsKey("RESTIC_REPOSITORY_FILE"));
            Assert.StartsWith(Path.GetDirectoryName(Path.GetFullPath(AppPaths.RcloneBinary))! + Path.PathSeparator, start.Environment["PATH"]);

            // A repository without a password (an unencrypted destination):
            // an inherited one must not reach restic either.
            var open = ResticRunner.StartInfo(new ResticCommand("restic.exe", Repository("", "--insecure-no-password"), ["check"]));
            Assert.False(open.Environment.ContainsKey("RESTIC_PASSWORD"));
            Assert.Equal("0", open.Environment["RESTIC_PROGRESS_FPS"]);
            Assert.Contains("--insecure-no-password", open.ArgumentList);
        }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
    }

    [Fact]
    public void The_log_line_shows_the_command_without_the_password_and_restic_text_is_redacted()
    {
        var start = ResticRunner.StartInfo(new ResticCommand(@"C:\Program Files\x\restic.exe", Repository(), ["backup", "--json", @"C:\My Files"]));
        var line = ResticRunner.TranscriptLine("info", "command: " + ResticRunner.Render(start.FileName, start.ArgumentList), new DateTime(2026, 11, 10, 6, 10, 43, 26, DateTimeKind.Utc));
        Assert.Equal("[restic] 06:10:43.026Z\tinfo\tcommand: \"C:\\Program Files\\x\\restic.exe\" backup --json \"C:\\My Files\" -o sftp.args=-oBatchMode=yes", line);
        Assert.DoesNotContain("horse", line);
        Assert.Equal("unable to open *** here", ResticRunner.Redact("unable to open correct horse battery staple here", "correct horse battery staple"));
        // Short values are not hunted for: they would cut words apart.
        Assert.Equal("abc", ResticRunner.Redact("abc", "ab"));
    }

    /// <summary>Recorded <c>restic backup --json</c> output (restic 0.19.1's
    /// shapes, doc/075_scripting.rst): each message type reaches the caller
    /// parsed once, file errors are counted and logged, the closing message
    /// becomes the reason, plain error output is detail.</summary>
    [Fact]
    public void Each_restic_message_type_is_read_as_what_it_is()
    {
        var command = new ResticCommand("restic.exe", Repository(), ["backup", "--json", "D:\\"]) { CaptureOutput = 1 << 20 };
        var reader = new ResticRunner.ResticOutput(command);
        var heard = new List<(string? Type, bool Error, bool Json)>();
        var log = new List<string>();
        void Feed(string text, bool stderr) => reader.Accept(text, cut: false, stderr, line =>
        {
            heard.Add((line.MessageType, line.StandardError, line.IsJson));
            if (line.MessageType == "status") Assert.Equal(0.25, line.Json.GetProperty("percent_done").GetDouble());
        }, log.Add);
        Feed("""{"message_type":"status","seconds_elapsed":1,"percent_done":0.25,"total_files":4,"files_done":1,"total_bytes":400,"bytes_done":100,"current_files":["D:\\a.txt"]}""", false);
        Feed("""{"message_type":"verbose_status","action":"new","item":"D:\\a.txt","duration":0.1,"data_size":100}""", false);
        Feed("""{"message_type":"error","error":{"message":"open D:\\locked.db: The process cannot access the file"},"during":"archival","item":"D:\\locked.db"}""", true);
        Feed("""{"message_type":"summary","files_new":3,"files_changed":0,"files_unmodified":0,"data_added":300,"total_files_processed":3,"total_bytes_processed":300,"total_duration":1.5,"snapshot_id":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}""", false);
        Feed("using parent snapshot 1234abcd", true);
        Feed("""{"message_type":"exit_error","code":3,"message":"Warning: at least one source file could not be read"}""", true);
        Assert.Equal(new (string?, bool, bool)[]
        {
            ("status", false, true), ("verbose_status", false, true), ("error", true, true), ("summary", false, true),
            (null, true, false), ("exit_error", true, true),
        }, heard);
        Assert.Equal(1, reader.FileErrors);
        Assert.Equal("Warning: at least one source file could not be read", reader.ExitMessage);
        // The file error and the closing message are shown as restic wrote
        // them; its plain error output is detail; status lines are not logged.
        Assert.Contains(log, l => l.StartsWith("[restic] {\"message_type\":\"error\"", StringComparison.Ordinal));
        Assert.Contains(log, l => l.StartsWith("[restic] {\"message_type\":\"exit_error\"", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("\tdebug\tusing parent snapshot 1234abcd", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains("\"status\"", StringComparison.Ordinal));
        Assert.Equal("Warning: at least one source file could not be read\nOutput: using parent snapshot 1234abcd (restic exit code 3)", reader.ErrorText(3, ResticRunner.Meaning(3)));
        Assert.Contains("\"summary\"", reader.Output);
        // The summary and status shapes give the recap and progress figures.
        var summary = BackupLiveProgress.FromResticSummary(System.Text.Json.JsonDocument.Parse(
            """{"message_type":"summary","files_new":3,"files_changed":1,"files_unmodified":5,"data_added":2048,"total_files_processed":9,"total_bytes_processed":4096,"total_duration":75,"snapshot_id":"b4f6658012345678"}""").RootElement)!;
        Assert.Equal("[recap] Backup complete: 9 files (4.0 KB) checked · 3 new, 1 changed, 5 unchanged · 2.0 KB added to the storage · took 1m 15s · snapshot b4f66580",
            BackupLiveProgress.RecapOf(summary));
        Assert.Equal((3, 1, 2048L), BackupLiveProgress.StatsOf(summary));
        var sample = BackupLiveProgress.FromResticStatus(System.Text.Json.JsonDocument.Parse(
            """{"message_type":"status","percent_done":0.5,"total_files":10,"files_done":5,"total_bytes":1000,"bytes_done":500,"current_files":["C:\\a","C:\\b"]}""").RootElement);
        Assert.Equal(new BackupLiveProgress.Sample(50, 1000, 500, 10, 5, "C:\\a\nC:\\b"), sample);
    }

    [Fact]
    public void A_text_command_without_a_closing_message_keeps_its_own_words_and_bounds_them()
    {
        var reader = new ResticRunner.ResticOutput(new ResticCommand("restic.exe", Repository(), ["forget"]) { OutputToTranscript = true });
        var log = new List<string>();
        reader.Accept("unable to create lock in backend: repository 4e1f is already locked by PID 1234", cut: false, standardError: true, null, log.Add);
        reader.Accept(new string('x', 5000), cut: false, standardError: true, null, log.Add);
        reader.Accept("keep 2 snapshots:", cut: false, standardError: false, null, log.Add);
        var text = reader.ErrorText(11, ResticRunner.Meaning(11));
        Assert.StartsWith("unable to create lock in backend: repository 4e1f is already locked by PID 1234\n", text);
        Assert.EndsWith(" (restic exit code 11)", text);
        Assert.Contains("more characters of restic's output are in the run's log", text);
        Assert.True(text.Length < ResticRunner.ErrorTextLimit + 200, $"{text.Length} characters");
        Assert.Equal("unable to create lock in backend: repository 4e1f is already locked by PID 1234", reader.Reason());
        // Plain output is detail, shown with Verbose.
        Assert.Contains(log, l => l.EndsWith("\tdebug\tkeep 2 snapshots:", StringComparison.Ordinal));
        // Nothing said at all: the meaning stands in.
        var silent = new ResticRunner.ResticOutput(new ResticCommand("restic.exe", Repository(), ["check"]));
        Assert.Equal("restic ended without saying why (the repository is locked) (restic exit code 11)", silent.ErrorText(11, ResticRunner.Meaning(11)));
    }

    [Fact]
    public async Task A_huge_line_is_cut_at_the_limit_and_an_unfinished_last_line_is_kept()
    {
        var huge = new string('h', 3 * ResticRunner.MaximumLineCharacters);
        var lines = new List<(string Text, bool Cut)>();
        await ResticRunner.ReadLinesAsync(new StringReader("first\r\n" + huge + "\nsecond\n{\"message_type\":\"sta"), ResticRunner.MaximumLineCharacters,
            (text, cut) => lines.Add((text, cut)));
        Assert.Equal(4, lines.Count);
        Assert.Equal(("first", false), lines[0]);
        Assert.Equal(ResticRunner.MaximumLineCharacters, lines[1].Text.Length);
        Assert.True(lines[1].Cut);
        Assert.Equal(("second", false), lines[2]);
        // restic ended in the middle of a line: kept, not parsed as JSON.
        Assert.Equal(("{\"message_type\":\"sta", false), lines[3]);
        var reader = new ResticRunner.ResticOutput(new ResticCommand("restic.exe", Repository(), ["backup"]));
        ResticLine? last = null;
        reader.Accept(lines[3].Text, lines[3].Cut, standardError: false, l => last = l, _ => { });
        Assert.Null(last!.Value.MessageType);
        Assert.False(last.Value.IsJson);
        // A cut JSON line is never parsed (it would be half a message).
        reader.Accept("{\"message_type\":\"status\"}", cut: true, standardError: false, l => last = l, _ => { });
        Assert.Null(last!.Value.MessageType);
    }

    [Theory]
    [InlineData(0, "success")]
    [InlineData(1, "failed")]
    [InlineData(2, "Go runtime error")]
    [InlineData(3, "some source data could not be read")]
    [InlineData(10, "no repository at this address")]
    [InlineData(11, "the repository is locked")]
    [InlineData(12, "wrong password")]
    [InlineData(130, "cancelled")]
    [InlineData(77, "unknown exit code")]
    public void Every_exit_code_restic_documents_has_its_meaning(int code, string meaning)
    {
        Assert.Equal(meaning, ResticRunner.Meaning(code));
        Assert.Equal("some snapshots could not be removed", ResticRunner.Meaning(3, "forget"));
    }

    // ---- real processes ----

    private static string Restic => AppPaths.ResticBinary;

    private static async Task<string> NewRepositoryAsync(string folder, string password)
    {
        var init = await ResticRunner.RunAsync(new ResticCommand(Restic, new ResticRepository(folder, password, [], []), ["init", "--json"]),
            null, null, CancellationToken.None);
        Assert.True(init.Succeeded, init.ErrorText);
        return folder;
    }

    private static string Source(string folder, int megabytes)
    {
        Directory.CreateDirectory(folder);
        for (var i = 0; i < megabytes; i++) File.WriteAllBytes(Path.Combine(folder, $"part{i:00}.bin"), RandomNumberGenerator.GetBytes(1 << 20));
        return folder;
    }

    /// <summary>The gentle stop on a real restic backup: Ctrl+Break reaches
    /// restic, which says so and removes its lock from the repository; it ends
    /// by itself (exit code 130) or, still finishing a throttled upload after
    /// the wait, is ended. Either way nothing of it is left running and no lock
    /// is left behind (a plain kill leaves one: this would fail).</summary>
    [Fact]
    public async Task A_real_restic_backup_asked_to_stop_removes_its_lock_and_leaves_nothing_running()
    {
        Assert.True(File.Exists(Restic), "restic is needed: " + Restic);
        using var ws = new TempWorkspace("runner-gentle");
        var password = Guid.NewGuid().ToString("N");
        var repository = new ResticRepository(await NewRepositoryAsync(ws.Sub("repo"), password), password, [], ["--limit-upload", "64"]);
        var source = Source(ws.Sub("source"), 6);
        using var stop = new CancellationTokenSource();
        var pid = 0;
        var log = new ConcurrentQueue<string>();
        var run = ResticRunner.RunAsync(new ResticCommand(Restic, repository, ["backup", "--json", source]) { ProgressFps = 2 }, line =>
        {
            if (line.MessageType == "status" && line.Json.TryGetProperty("bytes_done", out var done) && done.GetInt64() > 0) stop.Cancel();
        }, log.Enqueue, stop.Token, onStarted: p => pid = p);
        var result = await run.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        output.WriteLine(string.Join("\n", log));
        Assert.True(result.Cancelled);
        Assert.NotEqual(ResticStop.None, result.Stop);
        if (result.Stop == ResticStop.Gentle) Assert.Equal(130, result.ExitCode);
        Assert.Contains(log, l => l.EndsWith("\tdebug\tsignal interrupt received, cleaning up", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(Path.Combine(repository.Uri, "locks")));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        Assert.Contains(log, l => l.Contains("backup stopped on request", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Contains(password, StringComparison.Ordinal));
    }

    /// <summary>U196 (owner, 11 November 2026: "go ahead"): with Verbose off,
    /// a restic command that worked adds no line to the log (its command line
    /// and "finished: exit code 0" are Verbose detail); one that failed shows
    /// its command line and how it ended, as errors.</summary>
    [Fact]
    public async Task Only_a_failed_command_shows_its_command_line_without_Verbose()
    {
        Assert.True(File.Exists(Restic), "restic is needed: " + Restic);
        using var ws = new TempWorkspace("runner-levels");
        var password = Guid.NewGuid().ToString("N");
        var repository = new ResticRepository(await NewRepositoryAsync(ws.Sub("repo"), password), password, [], []);
        static string Level(string line) => line.Split('\t') is { Length: >= 3 } parts ? parts[1] : "";
        var worked = new ConcurrentQueue<string>();
        Assert.True((await ResticRunner.RunAsync(new ResticCommand(Restic, repository, ["snapshots"]), null, worked.Enqueue, TestContext.Current.CancellationToken)).Succeeded);
        output.WriteLine(string.Join("\n", worked));
        Assert.Contains(worked, l => l.Contains("\tcommand: ", StringComparison.Ordinal));
        Assert.All(worked.Where(l => l.StartsWith("[restic] ", StringComparison.Ordinal)), l => Assert.Equal("debug", Level(l)));

        var failed = new ConcurrentQueue<string>();
        var wrong = repository with { Password = "not-the-password" };
        Assert.Equal(12, (await ResticRunner.RunAsync(new ResticCommand(Restic, wrong, ["snapshots"]), null, failed.Enqueue, TestContext.Current.CancellationToken)).ExitCode);
        output.WriteLine(string.Join("\n", failed));
        Assert.Contains(failed, l => Level(l) == "error" && l.Contains("\tcommand: ", StringComparison.Ordinal) && l.Contains(" snapshots", StringComparison.Ordinal));
        Assert.Contains(failed, l => Level(l) == "error" && l.Contains("snapshots finished: exit code 12 (wrong password)", StringComparison.Ordinal));
        Assert.DoesNotContain(failed, l => l.Contains("not-the-password", StringComparison.Ordinal));
    }

    /// <summary>The fallback: a console program that shrugs Ctrl+Break off
    /// (ping -t prints its statistics and goes on) is ended once the wait is
    /// over, and the call returns only after it has.</summary>
    [Fact]
    public async Task A_program_that_does_not_stop_on_Ctrl_Break_is_ended_after_the_wait()
    {
        var wait = ResticRunner.GentleStopWait;
        ResticRunner.GentleStopWait = TimeSpan.FromSeconds(1);
        try
        {
            var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
            var start = new ProcessStartInfo(ping) { ArgumentList = { "-t", "127.0.0.1" } };
            using var stop = new CancellationTokenSource();
            var pid = 0;
            var lines = 0;
            var log = new ConcurrentQueue<string>();
            var run = ResticRunner.RunStartAsync(start, new ResticCommand(ping, Repository(""), ["ping"]), _ =>
            {
                if (Interlocked.Increment(ref lines) == 3) stop.Cancel();
            }, log.Enqueue, stop.Token, onStarted: p => pid = p);
            var watch = Stopwatch.StartNew();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            output.WriteLine(string.Join("\n", log));
            Assert.True(result.Cancelled);
            Assert.Equal(ResticStop.Ended, result.Stop);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.Elapsed}");
            Assert.Contains(log, l => l.Contains("did not end within 1.0 s and was ended", StringComparison.Ordinal));
        }
        finally { ResticRunner.GentleStopWait = wait; }
    }

    private static int? RcloneOf(int restic) => Backspace.Services.Platform.Windows.EngineTransfer.OnlyChild(restic, "rclone.exe");

    /// <summary>restic carries a cloud destination's data through rclone, a
    /// process it starts. A gentle stop lets rclone delete restic's lock for
    /// it, and restic ends rclone as it ends; ending the tree (Force quit)
    /// ends rclone too. A plain kill would leave rclone running. A local
    /// folder served by rclone stands in for the cloud. The gentle case runs
    /// without a bandwidth limit: a throttled upload in progress does not stop
    /// at once, so restic is then ended after the wait and its lock is cleared
    /// by the next exclusive command's unlock (the stale-lock rule).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_restic_on_an_rclone_destination_leaves_no_rclone_behind(bool force)
    {
        Assert.True(File.Exists(Restic) && File.Exists(AppPaths.RcloneBinary), "restic and rclone are needed");
        using var ws = new TempWorkspace("runner-rclone");
        var password = Guid.NewGuid().ToString("N");
        var folder = ws.Sub("rclone-repo").Replace('\\', '/');
        var repository = new ResticRepository("rclone::local:" + folder, password, [], force ? ["--limit-upload", "64"] : []);
        var init = await ResticRunner.RunAsync(new ResticCommand(Restic, repository, ["init", "--json"]), null, null, CancellationToken.None);
        Assert.True(init.Succeeded, init.ErrorText);
        var source = Source(ws.Sub("source"), force ? 6 : 400);
        using var stop = new CancellationTokenSource();
        var restic = 0;
        int? rclone = null;
        var log = new ConcurrentQueue<string>();
        var run = ResticRunner.RunAsync(new ResticCommand(Restic, repository, ["backup", "--json", source]) { ProgressFps = 2 }, line =>
        {
            if (rclone is null && line.MessageType == "status" && line.Json.TryGetProperty("bytes_done", out var done) && done.GetInt64() > 0
                && RcloneOf(restic) is { } child)
            {
                rclone = child;
                if (force) ResticRunner.EndAll(); else stop.Cancel();
            }
        }, log.Enqueue, stop.Token, onStarted: p => restic = p);
        var result = await run.WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.NotNull(rclone);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(restic));
        // Its rclone ended with it (not a moment later on its own).
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(rclone!.Value));
        output.WriteLine(string.Join(Environment.NewLine, log));
        if (force)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(log, l => l.Contains("ended by Force quit", StringComparison.Ordinal));
        }
        else
        {
            // rclone, in a group of its own, carried restic's last request:
            // deleting its lock.
            Assert.NotEqual(ResticStop.None, result.Stop);
            Assert.Contains(log, l => l.EndsWith("\tdebug\tsignal interrupt received, cleaning up", StringComparison.Ordinal));
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "locks")));
        }
    }
}
