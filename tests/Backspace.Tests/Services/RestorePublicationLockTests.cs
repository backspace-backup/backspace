using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class RestorePublicationLockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongRestoreLockOffersKeepWaitingOrStopWithoutLosingTheOriginal(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("restore-long-lock");
        var staged = Path.Combine(workspace.Root, "downloaded.txt");
        var target = workspace.Sub("target"); var output = Path.Combine(target, "file.txt");
        File.WriteAllText(staged, "RESTORED"); File.WriteAllText(output, "ORIGINAL");
        using var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var transaction = new RestoreOutputTransaction();
        var publish = Task.Run(() => transaction.Publish(staged, output, target, true, timeout.Token), timeout.Token);
        try
        {
            await Task.Delay(10_500, timeout.Token);
            Assert.False(publish.IsCompleted);
            var notice = Assert.Single(FileAccessRetry.Snapshot(), n => n.Path == staged);
            Assert.Equal("Waiting to place a restored file", notice.Title);
            Assert.Equal("Stop restore", notice.StopLabel);
            notice.KeepWaitingCommand.Execute(null);
            Assert.False(notice.ShowDetails);
            Assert.True(notice.CanStop);
            if (cancel)
            {
                notice.StopCommand.Execute(null);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publish.WaitAsync(TimeSpan.FromSeconds(3)));
                transaction.Rollback();
                Assert.Equal("ORIGINAL", File.ReadAllText(output));
                Assert.True(File.Exists(staged));
            }
            else
            {
                held.Dispose();
                await publish.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);
                Assert.Equal("RESTORED", File.ReadAllText(output));
                transaction.Rollback();
                Assert.Equal("ORIGINAL", File.ReadAllText(output));
            }
            Assert.DoesNotContain(FileAccessRetry.Snapshot(), n => n.Id == notice.Id);
        }
        finally
        {
            held.Dispose(); timeout.Cancel();
            try { await publish; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ShortLockOnDownloadedFileWaits_ThenPublishesTheVerifiedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("restore-publication-lock");
        var staged = Path.Combine(workspace.Root, "downloaded.txt");
        var target = workspace.Sub("target"); var output = Path.Combine(target, "file.txt");
        File.WriteAllText(staged, "RESTORED"); File.WriteAllText(output, "ORIGINAL");
        using var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        var transaction = new RestoreOutputTransaction();
        var publish = Task.Run(() => transaction.Publish(staged, output, target, true, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.False(publish.IsCompleted);
        held.Dispose();
        await publish.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("RESTORED", File.ReadAllText(output));
        transaction.Rollback();
        Assert.Equal("ORIGINAL", File.ReadAllText(output));
    }

    [Fact]
    public async Task CancelWhileDownloadedFileIsLockedRestoresTheOriginal()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("restore-publication-cancel");
        var staged = Path.Combine(workspace.Root, "downloaded.txt");
        var target = workspace.Sub("target"); var output = Path.Combine(target, "file.txt");
        File.WriteAllText(staged, "RESTORED"); File.WriteAllText(output, "ORIGINAL");
        using var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var transaction = new RestoreOutputTransaction();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => transaction.Publish(staged, output, target, true, stop.Token), TestContext.Current.CancellationToken));
        transaction.Rollback();
        Assert.Equal("ORIGINAL", File.ReadAllText(output));
        Assert.True(File.Exists(staged));
        Assert.Empty(Directory.GetDirectories(target, ".backspace-undo-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RollbackWaitsOutABriefScanOfTheSavedOriginal(bool persistent)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("restore-rollback-scan");
        var staged = Path.Combine(workspace.Root, "downloaded.txt");
        var target = workspace.Sub("target"); var output = Path.Combine(target, "file.txt");
        File.WriteAllText(staged, "RESTORED"); File.WriteAllText(output, "ORIGINAL");
        var transaction = new RestoreOutputTransaction();
        await Task.Run(() => transaction.Publish(staged, output, target, true, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var saved = Assert.Single(Directory.GetFiles(Assert.Single(Directory.GetDirectories(target, ".backspace-undo-*")), "*.original"));
        // A scanner opens the saved original without sharing deletion, which blocks its rename back.
        var scan = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (persistent)
            {
                var error = Assert.Throws<IOException>(transaction.Rollback);
                Assert.Contains(".backspace-undo-", error.Message);
                Assert.Equal("ORIGINAL", File.ReadAllText(saved));
            }
            else
            {
                var release = Task.Delay(300, TestContext.Current.CancellationToken).ContinueWith(_ => scan.Dispose(), TaskScheduler.Default);
                transaction.Rollback();
                await release;
                Assert.Equal("ORIGINAL", File.ReadAllText(output));
                Assert.Empty(Directory.GetDirectories(target, ".backspace-undo-*"));
            }
        }
        finally { scan.Dispose(); }
    }

    [Fact]
    public async Task FileCreatedDuringTheWaitIsNeverOverwritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("restore-publication-conflict");
        var staged = Path.Combine(workspace.Root, "downloaded.txt");
        var target = workspace.Sub("target"); var output = Path.Combine(target, "file.txt");
        File.WriteAllText(staged, "RESTORED");
        using var held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        var transaction = new RestoreOutputTransaction();
        var publish = Task.Run(() => transaction.Publish(staged, output, target, false, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.False(publish.IsCompleted);
        File.WriteAllText(output, "NEW USER FILE"); held.Dispose();
        await Assert.ThrowsAnyAsync<IOException>(() => publish.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        transaction.Rollback();
        Assert.Equal("NEW USER FILE", File.ReadAllText(output));
        Assert.Equal("RESTORED", File.ReadAllText(staged));
    }
}
