using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class FileAccessRetryTests
{
    [Theory]
    [InlineData("progress")]
    [InlineData("engine")]
    [InlineData("cleanup")]
    [InlineData("quit")]
    public async Task A_lock_lasting_over_ten_seconds_waits_then_recovers_without_losing_existing_data(string operation)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("long-file-wait");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(30));
        await using var sidecar = new BackrestSidecar("unused", "unused", workspace.Sub("engine"));
        var tasks = new SessionTaskControl(workspace.ConfigRoot, new Sessions());
        var stage = workspace.Sub("temporary-restore");
        var path = operation switch
        {
            "progress" => Path.Combine(workspace.Root, "progress.json"),
            "engine" => Path.Combine(sidecar.DataDir, "config.json"),
            "cleanup" => Path.Combine(stage, "temporary.txt"),
            _ => PreparePause(tasks, workspace.ConfigRoot)
        };
        if (operation == "engine") BackrestSidecar.WriteSeedConfig(path);
        else if (operation != "quit") File.WriteAllText(path, "previous confirmed contents");
        var previous = File.ReadAllBytes(path);
        var sibling = Path.Combine(workspace.Root, "unrelated-original.txt");
        File.WriteAllText(sibling, "must not change");
        SessionTaskControl.UnattendedLease? running = null;
        if (operation == "quit") Assert.True(tasks.TryBeginUnattended(out running));
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pending = Task.Run(() =>
        {
            switch (operation)
            {
                case "progress": TestRestoreContext.WriteAtomic(path, new { Saved = true }, stop.Token); break;
                case "engine": sidecar.PrepareAuthentication(stop.Token); break;
                case "cleanup": ResticBackrestEngine.DeleteRestoreStaging(stage, stop.Token); break;
                default: tasks.PauseForCurrentSession(ct: stop.Token); break;
            }
        });
        try
        {
            await Task.Delay(10_500, stop.Token);
            Assert.False(pending.IsCompleted, "A ten-second lock must still be waiting, not failed or reported successful.");
            Assert.Equal(previous, File.ReadAllBytes(path));
            var notice = Assert.Single(FileAccessRetry.Snapshot(), n => n.Path == (operation == "cleanup" ? stage : path));
            Assert.True(notice.CanStop);
            if (running is not null) Assert.False(running.Token.IsCancellationRequested,
                "Waiting to save Quit must not block the monitor and falsely cancel a running scheduled task.");
            held.Dispose();
            await pending.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
            Assert.DoesNotContain(FileAccessRetry.Snapshot(), n => n.Id == notice.Id);
            if (operation == "cleanup") Assert.False(Directory.Exists(stage));
            else Assert.NotEqual(previous, File.ReadAllBytes(path));
            if (running is not null)
            {
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!running.Token.IsCancellationRequested && DateTime.UtcNow < deadline) await Task.Delay(25, stop.Token);
                Assert.True(running.Token.IsCancellationRequested, "The saved Quit must then reach the existing task.");
            }
            Assert.Equal("must not change", File.ReadAllText(sibling));
        }
        finally
        {
            held.Dispose(); stop.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
            running?.Dispose();
        }
    }

    [Fact]
    public void A_directory_instead_of_a_save_file_and_invalid_data_do_not_retry_forever()
    {
        using var workspace = new TempWorkspace();
        var directory = workspace.Sub("not-a-file");
        Assert.True(Record.Exception(() => TestRestoreContext.WriteAtomic(directory, new { Saved = true })) is IOException or UnauthorizedAccessException);
        Assert.DoesNotContain(FileAccessRetry.Snapshot(), n => n.Path == directory);
        var attempts = 0;
        Assert.Throws<InvalidDataException>(() => FileAccessRetry.Run(() => { attempts++; throw new InvalidDataException("Unreadable contents"); },
            Path.Combine(workspace.Root, "invalid.json"), FileWaitReason.Progress));
        Assert.Equal(1, attempts);
        Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp"));
    }

    private static string PreparePause(SessionTaskControl tasks, string root)
    {
        tasks.PauseForCurrentSession(); tasks.ResumeOnGuiLaunch();
        return Directory.GetFiles(root, "task-pause-*.json").Single();
    }
    private sealed class Sessions : IInteractiveSessionReader
    {
        public bool Supported => true;
        public string AccountId => "disposable-long-lock-test";
        public InteractiveSessionIdentity? Current() => new(1, 1000, "Test account");
        public InteractiveSessionIdentity? Read(int id) => id == 1 ? Current() : null;
    }
}
