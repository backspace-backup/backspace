using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class RestoreSafetyRegressionTests
{
    [Fact]
    public async Task StagingCleanup_RetriesARealTemporaryWindowsFileLock()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("restore-cleanup-retry");
        var stage = workspace.Sub(".backspace-restore-test");
        var item = Path.Combine(stage, "temporary.txt");
        File.WriteAllText(item, "temporary download");
        var sibling = Path.Combine(workspace.Root, "keep.txt");
        File.WriteAllText(sibling, "unrelated original");
        using var held = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read);
        var cleanup = Task.Run(() => ResticEngine.DeleteRestoreStaging(stage), TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.False(cleanup.IsCompleted);
        held.Dispose();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(stage));
        Assert.Equal("unrelated original", File.ReadAllText(sibling));
    }

    [Fact]
    public void StagingCleanup_CancellationOfAWindowsLockPreservesTheFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("restore-cleanup-locked");
        var stage = workspace.Sub(".backspace-restore-test");
        var item = Path.Combine(stage, "temporary.txt");
        File.WriteAllText(item, "still locked");
        using var held = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Assert.ThrowsAny<OperationCanceledException>(() => ResticEngine.DeleteRestoreStaging(stage, stop.Token));
        Assert.True(File.Exists(item));
        Assert.Equal("still locked", File.ReadAllText(item));
    }

    /// <summary>A junction chosen as the target, or above it, is refused
    /// before restic is even asked for the snapshot (this engine has no
    /// restic, so any start would show).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedTargetOrAncestor_IsRejectedBeforeEngineSubmission(bool nestedTarget)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace("restore-junction-target");
        var outside = workspace.Sub("outside");
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "UNTOUCHED");
        var junction = Path.Combine(workspace.Root, "redirect");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe") {
            Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var engine = new ResticEngine(new SecretsStore(), Path.Combine(workspace.Root, "no-restic.exe"));
            var commands = 0;
            engine.ResticCommandFinished += (_, _) => commands++;
            var target = nestedTarget ? Path.Combine(junction, "new-child") : junction;
            await Assert.ThrowsAsync<IOException>(() => engine.RestoreFilesAsync(new Backup(), Path.Combine(workspace.Root, "source"),
                new Destination { Id = "restore-probe" }, "default", 1, new[] { "keep.txt" }, target, null,
                TestContext.Current.CancellationToken, new[] { "keep.txt" }));
            Assert.Equal(0, commands);
            Assert.Equal("UNTOUCHED", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.False(Directory.Exists(Path.Combine(outside, "new-child")));
            Assert.Empty(Directory.GetDirectories(outside, ".backspace-restore-*"));
            ResticEngine.ValidateRestoreTarget(Path.Combine(workspace.Root, "ordinary", "new-child"));
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("file.txt\n/other.txt")]
    public void UnsafeSnapshotPaths_AreRejected(string relative)
        => Assert.Throws<InvalidOperationException>(() => ResticEngine.NormalizeRestoreRelativePath(relative));

    [Fact]
    public void LiveProgress_UsesBytesAndSkippedCounts()
    {
        Assert.True(ResticEngine.TryParseRestoreProgress(
            "{\"message_type\":\"status\",\"total_files\":8,\"files_restored\":2,\"files_skipped\":1,\"total_bytes\":100,\"bytes_restored\":40,\"percent_done\":0.4}", out var progress));
        Assert.Equal(8, progress.TotalFiles);
        Assert.Equal(3, progress.FilesRestored);
        Assert.Equal(40, progress.BytesRestored);
        Assert.False(ResticEngine.TryParseRestoreProgress("not a progress line", out _));
    }

    [Fact]
    public void RestoreCompletion_RejectsErroredLeaf()
    {
        var complete = ResticEngine.ParseCompletedRestoreLeaves(
            "{\"message_type\":\"verbose_status\",\"action\":\"restored\",\"item\":\"/good.txt\"}\n" +
            "{\"message_type\":\"verbose_status\",\"action\":\"restored\",\"item\":\"/bad.txt\"}\n" +
            "{\"message_type\":\"error\",\"item\":\"/bad.txt\",\"error\":{\"message\":\"write failed\"}}\n");
        Assert.Contains("good.txt", complete);
        Assert.DoesNotContain("bad.txt", complete);
    }
}
