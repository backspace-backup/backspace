using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Models;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class SessionTaskControlTests
{
    [Fact]
    public async Task A_cancelled_startup_resume_cannot_clear_a_newer_Quit_pause()
    {
        using var workspace = new TempWorkspace();
        var control = new SessionTaskControl(workspace.ConfigRoot, new Sessions());
        control.PauseForCurrentSession();
        var path = Directory.GetFiles(workspace.ConfigRoot, "task-pause-*.json").Single();
        using var stop = new CancellationTokenSource();
        Task resume;
        // StateFileLock is thread-owned; do not await while holding it.
        using (StateFileLock.Acquire(path))
        {
            resume = Task.Run(() => control.ResumeOnGuiLaunch(stop.Token));
            stop.Cancel();
            control.PauseForCurrentSession();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resume.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(control.TryBeginUnattended(out _));
    }

    private sealed class Sessions : IInteractiveSessionReader
    {
        public bool Supported => true;
        public string AccountId { get; init; } = "account-one";
        public InteractiveSessionIdentity? Session = new(1, 1000, "TEST\\ACCOUNT");
        public InteractiveSessionIdentity? Current() => Session;
        public InteractiveSessionIdentity? Read(int id) => Session?.SessionId == id ? Session : null;
    }

    [Fact]
    public void Quit_blocks_all_new_tasks_until_GUI_reopen_and_does_not_disable_the_schedule()
    {
        using var ws = new TempWorkspace();
        var sessions = new Sessions();
        var gui = new SessionTaskControl(ws.ConfigRoot, sessions);
        var scheduled = new SessionTaskControl(ws.ConfigRoot, sessions);
        Assert.True(scheduled.TryBeginUnattended(out var initial)); initial!.Dispose();
        gui.PauseForCurrentSession();
        Assert.False(scheduled.TryBeginUnattended(out var blocked)); Assert.Null(blocked);
        gui.ResumeOnGuiLaunch();
        Assert.True(scheduled.TryBeginUnattended(out var resumed)); resumed!.Dispose();
        Assert.Empty(Directory.GetFiles(ws.ConfigRoot, "config.json"));
    }

    [Theory]
    [InlineData("quit")]
    [InlineData("reopen")]
    [InlineData("cancel-quit")]
    public async Task A_brief_windows_replacement_lock_does_not_lose_a_pause_state_change(string action)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        var generation = control.PauseForCurrentSession();
        if (action == "quit") control.ResumeOnGuiLaunch();
        var path = Directory.GetFiles(ws.ConfigRoot, "task-pause-*.json").Single();
        var previous = File.ReadAllBytes(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var change = Task.Run(() =>
        {
            if (action == "quit") control.PauseForCurrentSession();
            else if (action == "reopen") control.ResumeOnGuiLaunch();
            else control.CancelQuit(generation);
        });
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(change.IsCompleted, "A blocked replacement cannot report that the state was saved.");
        Assert.Equal(previous, File.ReadAllBytes(path));
        held.Dispose();
        await change.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.NotEqual(previous, File.ReadAllBytes(path));
        Assert.Equal(action != "quit", control.TryBeginUnattended(out var lease));
        lease?.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Signout_or_reused_session_ID_ends_pause(bool reusedId)
    {
        using var ws = new TempWorkspace();
        var sessions = new Sessions();
        var control = new SessionTaskControl(ws.ConfigRoot, sessions);
        control.PauseForCurrentSession();
        Assert.False(control.TryBeginUnattended(out _));
        sessions.Session = reusedId ? sessions.Session! with { LogonTime = 2000 } : null;
        Assert.True(control.TryBeginUnattended(out var allowed)); allowed!.Dispose();
    }

    [Fact]
    public void Another_account_is_not_paused_and_a_stale_quit_cannot_undo_a_newer_quit()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        var first = control.PauseForCurrentSession();
        control.PauseForCurrentSession();
        control.CancelQuit(first);
        Assert.False(control.TryBeginUnattended(out _));
        var other = new SessionTaskControl(ws.ConfigRoot, new Sessions { AccountId = "different-account" });
        Assert.True(other.TryBeginUnattended(out var allowed)); allowed!.Dispose();
    }

    [Fact]
    public async Task Running_task_observes_quit_even_if_GUI_reopens_before_its_next_check()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        Assert.True(control.TryBeginUnattended(out var active));
        using (active)
        {
            Assert.True(new SessionTaskControl(ws.ConfigRoot, new Sessions()).HasRunningTasks);
            control.PauseForCurrentSession();
            control.ResumeOnGuiLaunch();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(5), active!.Token));
            // Cancellation alone must not release the engine-lifetime handle.
            Assert.True(control.HasRunningTasks);
            Assert.True(control.TryBeginUnattended(out var newRun));
            Assert.False(newRun!.Token.IsCancellationRequested); newRun.Dispose();
        }
        Assert.False(control.HasRunningTasks);
    }

    [Fact]
    public async Task Force_quit_still_reaches_a_task_already_stopping_from_normal_quit()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        var forced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(control.TryBeginUnattended(out var active, () => { forced.TrySetResult(); return Task.CompletedTask; }));
        using (active)
        {
            control.PauseForCurrentSession();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(TimeSpan.FromSeconds(5), active!.Token));
            Assert.False(forced.Task.IsCompleted);
            control.PauseForCurrentSession(force: true);
            await forced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Unreadable_state_is_preserved_and_task_admission_fails_closed()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        control.PauseForCurrentSession();
        var path = Directory.GetFiles(ws.ConfigRoot, "task-pause-*.json").Single();
        File.WriteAllText(path, "unreadable saved record");
        Assert.ThrowsAny<Exception>(() => control.TryBeginUnattended(out _));
        Assert.ThrowsAny<Exception>(() => control.ResumeOnGuiLaunch());
        Assert.Equal("unreadable saved record", File.ReadAllText(path));
    }

    [Fact]
    public void Cancelling_a_blocked_pause_write_does_not_report_a_successful_quit_or_damage_prior_state()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        control.PauseForCurrentSession(); control.ResumeOnGuiLaunch();
        var path = Directory.GetFiles(ws.ConfigRoot, "task-pause-*.json").Single();
        var before = File.ReadAllText(path);
        using (var denyReplacement = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            Assert.ThrowsAny<OperationCanceledException>(() => control.PauseForCurrentSession(ct: stop.Token));
        }
        Assert.Equal(before, File.ReadAllText(path));
        Assert.True(control.TryBeginUnattended(out var allowed)); allowed!.Dispose();
    }

    [Fact]
    public async Task Real_Windows_session_pause_rejects_every_CLI_work_mode_before_reading_config()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var ws = new TempWorkspace();
        if (new Backspace.Services.Platform.Windows.WindowsInteractiveSessionReader().Current() is null)
            Assert.Skip("This native CLI check requires a signed-in Windows desktop; hosted service sessions cannot provide it.");
        var control = new SessionTaskControl();
        Assert.NotNull(control.PauseForCurrentSession());
        // A wrong entry path would read this broken config and fail, rather
        // than returning the intentional paused result before initialization.
        File.WriteAllText(Path.Combine(ws.ConfigRoot, "config.json"), "deliberately invalid config");
        var assembly = typeof(SessionTaskControl).Assembly.Location;
        async Task<(int code, string output)> Invoke(string command)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(assembly); start.ArgumentList.Add(command);
            if (command != "--run-all") start.ArgumentList.Add("Disposable backup");
            start.Environment["BACKSPACE_CONFIG_ROOT"] = ws.ConfigRoot;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return (process.ExitCode, await output + await error);
        }
        foreach (var mode in new[] { "--run", "--run-all", "--check", "--prune" })
        {
            var result = await Invoke(mode);
            Assert.True(result.code == 0, result.output);
            Assert.Contains(SessionTaskControl.PausedMessage, result.output);
        }
        Assert.False(Directory.Exists(Path.Combine(ws.ConfigRoot, "backrest")));
        Assert.Equal("deliberately invalid config", File.ReadAllText(Path.Combine(ws.ConfigRoot, "config.json")));
        control.ResumeOnGuiLaunch();
        var unpaused = await Invoke("--run");
        Assert.Equal(2, unpaused.code);
        Assert.Contains("No backup named", unpaused.output);
        Assert.DoesNotContain(SessionTaskControl.PausedMessage, unpaused.output);
    }

    [Fact]
    public void Real_Windows_session_reader_distinguishes_the_current_and_nonexistent_sessions()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reader = new Backspace.Services.Platform.Windows.WindowsInteractiveSessionReader();
        var current = reader.Current();
        if (current is null) Assert.Skip("This native session check requires a signed-in Windows desktop.");
        Assert.NotNull(current);
        Assert.Equal(Process.GetCurrentProcess().SessionId, current.SessionId);
        Assert.True(current.LogonTime > 0);
        Assert.Equal(current, reader.Read(current.SessionId));
        Assert.Null(reader.Read(int.MaxValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_scheduled_backup_stops_its_owned_engine_when_Quit_is_requested(bool force)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (new Backspace.Services.Platform.Windows.WindowsInteractiveSessionReader().Current() is null)
            Assert.Skip("The native Quit check requires a signed-in Windows desktop.");
        using var ws = new TempWorkspace();
        var source = ws.Sub("source");
        // Incompressible data plus the actual restic upload limit gives the
        // test time to interrupt a real repository operation, not a fake delay.
        var bytes = new byte[1024 * 1024]; new Random(71).NextBytes(bytes);
        using (var file = File.Create(Path.Combine(source, "large-file.bin")))
            for (var i = 0; i < 64; i++) { bytes[0] = (byte)i; file.Write(bytes); }
        var config = new ConfigStore(); config.Load();
        var secrets = new SecretsStore();
        var destination = new Destination { Name = "Disposable scheduled repository", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = ws.Sub("repository"), Encrypted = true,
            StoragePasswordRef = secrets.PutNew("session-test", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Disposable scheduled backup", SourcePaths = { source }, UseVss = false,
            VssDisabledAcknowledged = true, BandwidthLimitKBps = 64,
            CheckSchedule = new BackupSchedule(), PruneSchedule = new BackupSchedule(),
            Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(destination); });
        var start = new ProcessStartInfo(Path.ChangeExtension(typeof(SessionTaskControl).Assembly.Location, ".exe")) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--run"); start.ArgumentList.Add(backup.Name);
        start.Environment["BACKSPACE_CONFIG_ROOT"] = ws.ConfigRoot;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            OwnedProcessRegistry.Ownership? engine = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var engineRoot = Path.Combine(ws.ConfigRoot, "engine-runs");
            var locks = Path.Combine(destination.PathOrSubpath, "locks");
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                if (Directory.Exists(engineRoot))
                    engine = Directory.GetFiles(engineRoot, "sidecar.pid", SearchOption.AllDirectories)
                        .Select(OwnedProcessRegistry.Read).FirstOrDefault(x => x?.Owner.Pid == process.Id);
                if (engine is not null && Directory.Exists(locks) && Directory.GetFiles(locks).Length > 0) break;
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            Assert.False(process.HasExited, process.HasExited ? await output + await error
                + string.Join("\n", new LogStore().LoadHistory(backup.Name).SelectMany(r => r.Errors)) : "");
            Assert.NotNull(engine);
            Assert.True(Directory.Exists(locks) && Directory.GetFiles(locks).Length > 0, "A real restic repository operation must have started before Quit.");
            Assert.Equal(OwnedProcessRegistry.Presence.Matching, OwnedProcessRegistry.Inspect(engine.Child));
            // Keep the controller on the same disposable config as the child.
            // The default constructor can resolve a registered desktop service
            // and would otherwise read or pause a different task set.
            var control = new SessionTaskControl(ws.ConfigRoot,
                new Backspace.Services.Platform.Windows.WindowsInteractiveSessionReader());
            Assert.True(control.HasRunningTasks);
            control.PauseForCurrentSession(force);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
            Assert.False(control.HasRunningTasks);
            Assert.Contains(OwnedProcessRegistry.Inspect(engine.Child), new[] { OwnedProcessRegistry.Presence.Missing, OwnedProcessRegistry.Presence.Different });
            Assert.False(control.TryBeginUnattended(out _));
            Assert.DoesNotContain(": Success -", await output);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await error;
        }
    }
}
