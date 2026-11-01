using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class RestoreControlTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_while_a_pause_change_is_waiting_cannot_resurrect_the_completed_transfer(bool resume)
    {
        var control = new RestoreControl(paused: resume);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void Block()
        {
            if (control.State == RestoreControlState.Complete) return;
            entered.Set(); release.Wait(TestContext.Current.CancellationToken);
            throw new OperationCanceledException("The pending save was stopped");
        }
        control.Changed += Block;
        var change = Task.Run(() => { if (resume) control.Resume(); else control.Pause(); });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            control.Complete(cancelled: true);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
            Assert.Equal(RestoreControlState.Complete, control.State);
        }
        finally { release.Set(); control.Changed -= Block; }
    }

    [Fact]
    public async Task PauseRequiresTransferAcknowledgement_ResumeUsesANewToken_CancelBreaksTheWait()
    {
        var control = new RestoreControl();
        using var first = control.TransferCancellation(TestContext.Current.CancellationToken);
        control.Pause();
        Assert.True(first.IsCancellationRequested);
        Assert.Equal(RestoreControlState.Pausing, control.State);
        control.Resume(); // Cannot pretend an unacknowledged transfer is paused.
        Assert.Equal(RestoreControlState.Pausing, control.State);
        var wait = control.WaitWhilePausedAsync(TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted); Assert.Equal(RestoreControlState.Paused, control.State);
        control.Resume(); await wait;
        using var next = control.TransferCancellation(TestContext.Current.CancellationToken);
        Assert.False(next.IsCancellationRequested);
        control.Pause();
        using var cancelled = new CancellationTokenSource();
        var paused = control.WaitWhilePausedAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paused);
        control.Complete(cancelled: true);
        Assert.Equal(RestoreControlState.Complete, control.State);
    }

    [Fact]
    public void FailedPausePersistence_DoesNotStopTheTransfer()
    {
        var control = new RestoreControl();
        using var transfer = control.TransferCancellation(TestContext.Current.CancellationToken);
        void Fail() => throw new IOException("locked journal");
        control.Changed += Fail;
        Assert.Throws<IOException>(control.Pause);
        Assert.False(transfer.IsCancellationRequested); Assert.Equal(RestoreControlState.Running, control.State);
        control.Changed -= Fail; control.Complete(cancelled: true);
    }

    [Fact]
    public void CancelRestoresReplacedOriginals_AndRemovesOnlyItsNewFilesAndFolders()
    {
        using var space = new TempWorkspace("cancel-output");
        var target = space.Sub("target"); var stage = space.Sub("staged");
        File.WriteAllText(Path.Combine(target, "same.txt"), "ORIGINAL");
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "KEEP ME");
        File.WriteAllText(Path.Combine(stage, "old.txt"), "RESTORED OLD VERSION");
        File.WriteAllText(Path.Combine(stage, "new.txt"), "NEW RESTORED FILE");
        var output = new RestoreOutputTransaction();
        output.Publish(Path.Combine(stage, "old.txt"), Path.Combine(target, "same.txt"), target, true, TestContext.Current.CancellationToken);
        output.Publish(Path.Combine(stage, "new.txt"), Path.Combine(target, "nested", "new.txt"), target, true, TestContext.Current.CancellationToken);
        Assert.Equal("RESTORED OLD VERSION", File.ReadAllText(Path.Combine(target, "same.txt")));
        Assert.Single(Directory.GetFiles(target, "recovery.jsonl", SearchOption.AllDirectories));
        output.Rollback();
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(target, "same.txt")));
        Assert.Equal("KEEP ME", File.ReadAllText(Path.Combine(target, "unrelated.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "nested")));
        Assert.Empty(Directory.GetDirectories(target));
    }

    [Fact]
    public void CancelPreservesAConcurrentEdit_AndKeepsTheOriginalForRecovery()
    {
        using var space = new TempWorkspace("cancel-edited-output");
        var target = space.Sub("target"); var stage = space.Sub("staged");
        var existing = Path.Combine(target, "same.txt"); var staged = Path.Combine(stage, "same.txt");
        File.WriteAllText(existing, "ORIGINAL"); File.WriteAllText(staged, "RESTORED");
        var output = new RestoreOutputTransaction();
        output.Publish(staged, existing, target, true, TestContext.Current.CancellationToken);
        File.WriteAllText(existing, "EDITED BY ANOTHER PROGRAM");
        var error = Assert.Throws<IOException>(output.Rollback);
        Assert.Contains("both versions were preserved", error.Message);
        Assert.Equal("EDITED BY ANOTHER PROGRAM", File.ReadAllText(existing));
        Assert.Equal("ORIGINAL", File.ReadAllText(Assert.Single(Directory.GetFiles(target, "*.original", SearchOption.AllDirectories))));
        Assert.Single(Directory.GetFiles(target, "recovery.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public void SuccessfulCommitKeepsRestoredFiles_AndRemovesTheRecoveryCopies()
    {
        using var space = new TempWorkspace("commit-output");
        var target = space.Sub("target"); var stage = space.Sub("staged");
        var existing = Path.Combine(target, "same.txt"); var staged = Path.Combine(stage, "same.txt");
        File.WriteAllText(existing, "ORIGINAL"); File.WriteAllText(staged, "RESTORED");
        var output = new RestoreOutputTransaction();
        output.Publish(staged, existing, target, true, TestContext.Current.CancellationToken);
        output.Commit();
        Assert.Equal("RESTORED", File.ReadAllText(existing)); Assert.Empty(Directory.GetDirectories(target));
        output.Rollback(); Assert.Equal("RESTORED", File.ReadAllText(existing));
    }
}
