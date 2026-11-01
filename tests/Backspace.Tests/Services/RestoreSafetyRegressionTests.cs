using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
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
        var cleanup = Task.Run(() => ResticBackrestEngine.DeleteRestoreStaging(stage), TestContext.Current.CancellationToken);
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
        Assert.ThrowsAny<OperationCanceledException>(() => ResticBackrestEngine.DeleteRestoreStaging(stage, stop.Token));
        Assert.True(File.Exists(item));
        Assert.Equal("still locked", File.ReadAllText(item));
    }

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
            using var fixture = new ProtocolFixture("STATUS_SUCCESS", stage => File.WriteAllText(Path.Combine(stage, "keep.txt"), "REPLACED"));
            var target = nestedTarget ? Path.Combine(junction, "new-child") : junction;
            await Assert.ThrowsAsync<IOException>(() => fixture.Restore(target, new[] { "keep.txt" }));
            Assert.Null(fixture.Handler.LastEndpoint);
            Assert.Equal("UNTOUCHED", File.ReadAllText(Path.Combine(outside, "keep.txt")));
            Assert.False(Directory.Exists(Path.Combine(outside, "new-child")));
            Assert.Empty(Directory.GetDirectories(outside, ".backspace-restore-*"));
            ResticBackrestEngine.ValidateRestoreTarget(Path.Combine(workspace.Root, "ordinary", "new-child"));
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("STATUS_SUCCESS", RestoreTerminalStatus.Success)]
    [InlineData("STATUS_ERROR", RestoreTerminalStatus.Error)]
    [InlineData("STATUS_USER_CANCELLED", RestoreTerminalStatus.Cancelled)]
    public async Task NativeRestore_PreservesLayoutAndOriginals_AndReadsStructuredProgress(string terminal, RestoreTerminalStatus expected)
    {
        using var workspace = new TempWorkspace("native-restore-safety");
        var target = workspace.Sub("target");
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        File.WriteAllText(Path.Combine(target, "nested", "picked[1].txt"), "original");
        File.WriteAllText(Path.Combine(target, "nested", "pickedX1Y.txt"), "unselected original");
        using var fixture = new ProtocolFixture(terminal, stage =>
        {
            File.WriteAllText(Path.Combine(stage, "picked[1].txt"), "complete");
            File.WriteAllText(Path.Combine(stage, "pickedX1Y.txt"), "overmatch");
        });
        fixture.Handler.ProgressPolls = 2;
        var updates = new List<RestoreProgressUpdate>();
        var result = await fixture.Restore(target, new[] { "nested/picked[1].txt" }, onProgress: updates.Add, native: true);
        Assert.Equal(expected, result.Status);
        Assert.Equal(terminal == "STATUS_SUCCESS" ? "complete" : "original", File.ReadAllText(Path.Combine(target, "nested", "picked[1].txt")));
        Assert.Equal("unselected original", File.ReadAllText(Path.Combine(target, "nested", "pickedX1Y.txt")));
        Assert.False(File.Exists(Path.Combine(target, "picked[1].txt")));
        Assert.Contains(updates, p => p.TotalBytes == 8 && p.BytesRestored == 4);
        Assert.Empty(Directory.GetDirectories(target, ".backspace-restore-*"));
        Assert.Equal("/v1.Backrest/Restore", fixture.Handler.LastEndpoint);
        Assert.EndsWith("/nested/picked[1].txt", fixture.Handler.NativePath);
        Assert.DoesNotContain("picked?1?", fixture.Handler.NativePath);
    }

    [Theory]
    [InlineData("/v1.Backrest/DoRepoTask")]
    [InlineData("/v1.Backrest/Forget")]
    [InlineData("/v1.Backrest/RunCommand")]
    public async Task ScheduledRepositoryRpc_UsesReturnedId_AndWaitsForRecordedOperation(string endpoint)
    {
        using var fixture = new ProtocolFixture("STATUS_SUCCESS");
        fixture.Handler.DiscoveryDelayPolls = 2;
        fixture.Handler.IncludeOtherMatchingOperation = true;
        var terminal = await fixture.Track(endpoint, TestContext.Current.CancellationToken);
        Assert.Equal("42", terminal.Id);
        Assert.Equal("STATUS_SUCCESS", terminal.Status);
        Assert.Equal(0, fixture.Handler.DiscoveryDelayPolls);
        Assert.Equal(0, fixture.Handler.CancelRequests);
    }

    [Theory]
    [InlineData("/v1.Backrest/DoRepoTask")]
    [InlineData("/v1.Backrest/Forget")]
    public async Task ScheduledRepositoryRpc_DelayedAcknowledgement_CannotAdoptAnotherOperation(string endpoint)
    {
        using var fixture = new ProtocolFixture("STATUS_SUCCESS");
        fixture.Handler.DelayAcknowledgement = true;
        fixture.Handler.IncludeOtherMatchingOperation = true;
        var terminal = await fixture.Track(endpoint, TestContext.Current.CancellationToken);
        Assert.Equal("42", terminal.Id);
        Assert.Equal("STATUS_SUCCESS", terminal.Status);
        Assert.True(fixture.Handler.AcknowledgementReleased);
        Assert.Equal(0, fixture.Handler.CancelRequests);
    }

    [Theory]
    [InlineData("/v1.Backrest/DoRepoTask")]
    [InlineData("/v1.Backrest/Forget")]
    public async Task ScheduledRepositoryRpc_CancellationDuringDelayedAcknowledgement_CancelsOnlyReturnedId(string endpoint)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ProtocolFixture("STATUS_INPROGRESS", cancel: cancellation);
        fixture.Handler.DelayAcknowledgement = true;
        fixture.Handler.IncludeOtherMatchingOperation = true;
        var terminal = await fixture.Track(endpoint, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(fixture.Handler.AcknowledgementReleased);
        Assert.Equal("42", Assert.Single(fixture.Handler.CancelledIds));
        Assert.True(fixture.Handler.TerminalAcknowledged);
        Assert.Equal("42", terminal.Id);
        Assert.Equal("STATUS_USER_CANCELLED", terminal.Status);
    }

    [Fact]
    public async Task NativeRestore_UsesReturnedOperationId_DespiteDelayedVisibilityAndSimilarOperation()
    {
        using var workspace = new TempWorkspace("native-restore-returned-id");
        using var fixture = new ProtocolFixture("STATUS_SUCCESS", stage => File.WriteAllText(Path.Combine(stage, "picked.txt"), "complete"));
        fixture.Handler.DiscoveryDelayPolls = 2;
        fixture.Handler.IncludeOtherMatchingOperation = true;
        var result = await fixture.Restore(workspace.Sub("target"), new[] { "picked.txt" });
        Assert.Equal(RestoreTerminalStatus.Success, result.Status);
        Assert.Equal(1, result.FilesRestored);
        Assert.Equal(0, fixture.Handler.DiscoveryDelayPolls);
        Assert.Equal(0, fixture.Handler.CancelRequests);
    }

    [Fact]
    public async Task NativeRestore_CancellationBeforeAcknowledgement_WaitsForIdAndCancelsOnlyThatOperation()
    {
        using var workspace = new TempWorkspace("native-restore-delayed-acknowledgement");
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ProtocolFixture("STATUS_INPROGRESS", stage => File.WriteAllText(Path.Combine(stage, "picked.txt"), "partial"));
        fixture.Handler.DelayNativeAcknowledgement = true;
        fixture.Handler.IncludeOtherMatchingOperation = true;
        var target = workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "picked.txt"), "original");
        var restore = fixture.Restore(target, new[] { "picked.txt" }, cancellation.Token);
        await fixture.Handler.NativeSubmitted.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.False(restore.IsCompleted);
        Assert.Equal(0, fixture.Handler.CancelRequests);
        fixture.Handler.ReleaseAcknowledgement();
        var result = await restore;
        Assert.Equal(RestoreTerminalStatus.Cancelled, result.Status);
        Assert.Equal("42", Assert.Single(fixture.Handler.CancelledIds));
        Assert.True(fixture.Handler.StagingExistedWhenCancelled);
        Assert.True(fixture.Handler.TerminalAcknowledged);
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "picked.txt")));
        Assert.Empty(Directory.GetDirectories(target));
    }

    [Fact]
    public async Task SnapshotListings_UseConfiguredRepositoryId_ForBothTreeAndDirectoryRequests()
    {
        using var fixture = new ProtocolFixture("STATUS_SUCCESS");
        var tree = await fixture.ListFiles();
        Assert.Equal("kept.txt", Assert.Single(tree).Path);
        var directory = await fixture.ListDirectory();
        Assert.Equal("kept.txt", Assert.Single(directory!).Name);
        Assert.Equal(new[] { "/" + ResticBackrestEngine.SourcePathToSnapshotPrefix(ProtocolFixture.Source),
            "/" + ResticBackrestEngine.SourcePathToSnapshotPrefix(ProtocolFixture.Source) }, fixture.Handler.ListedPaths);
    }

    [Fact]
    public async Task SnapshotListing_StartsAtSource_VisitsNestedFolders_AndExcludesSimilarlyNamedSiblings()
    {
        using var fixture = new ProtocolFixture("STATUS_SUCCESS");
        fixture.Handler.NestedListing = true;
        var tree = await fixture.ListFiles();
        Assert.Equal(new[] { "kept.txt", "nested/café [1].txt" }, System.Linq.Enumerable.Select(tree, f => f.Path));
        var prefix = "/" + ResticBackrestEngine.SourcePathToSnapshotPrefix(ProtocolFixture.Source);
        Assert.Equal(new[] { prefix, prefix + "/nested", prefix + "/nested/empty" }, fixture.Handler.ListedPaths);
        Assert.DoesNotContain("/", fixture.Handler.ListedPaths);
    }

    [Fact]
    public async Task NativeRestore_CancellationWaitsForDaemon_AndPreservesOriginal()
    {
        using var workspace = new TempWorkspace("native-restore-cancel");
        using var cancellation = new CancellationTokenSource();
        var target = workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "original");
        using var fixture = new ProtocolFixture("STATUS_INPROGRESS", stage => File.WriteAllText(Path.Combine(stage, "keep.txt"), "partial"), cancellation);
        var result = await fixture.Restore(target, new[] { "keep.txt" }, cancellation.Token, native: true);
        Assert.Equal(RestoreTerminalStatus.Cancelled, result.Status);
        Assert.Equal(1, fixture.Handler.CancelRequests);
        Assert.True(fixture.Handler.TerminalAcknowledged);
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Empty(Directory.GetDirectories(target));
    }

    [Fact]
    public async Task NativeRestore_EmptySuccessIsAnError()
    {
        using var workspace = new TempWorkspace("native-restore-empty");
        using var fixture = new ProtocolFixture("STATUS_SUCCESS");
        var result = await fixture.Restore(workspace.Sub("target"), new[] { "missing.txt" }, native: true);
        Assert.Equal(RestoreTerminalStatus.Error, result.Status);
        Assert.Equal(0, result.FilesRestored);
    }

    [Fact]
    public async Task BracketRestore_PromotesOnlyRequestedOutput_AndPreservesOccupiedTarget()
    {
        using var workspace = new TempWorkspace("restore-bracket-preservation");
        var target = workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "unrelated.txt"), "keep original");
        File.WriteAllText(Path.Combine(target, "selectedXdraftY.txt"), "keep sibling");
        using var fixture = new ProtocolFixture("STATUS_SUCCESS", stage =>
        {
            File.WriteAllText(Path.Combine(stage, "selected[draft].txt"), "selected snapshot");
            File.WriteAllText(Path.Combine(stage, "selectedXdraftY.txt"), "wildcard overmatch");
        });
        var result = await fixture.Restore(target, new[] { "selected[draft].txt" });
        Assert.Equal(RestoreTerminalStatus.Success, result.Status);
        Assert.Equal(1, result.FilesRestored);
        Assert.Equal("selected snapshot", File.ReadAllText(Path.Combine(target, "selected[draft].txt")));
        Assert.Equal("keep original", File.ReadAllText(Path.Combine(target, "unrelated.txt")));
        Assert.Equal("keep sibling", File.ReadAllText(Path.Combine(target, "selectedXdraftY.txt")));
        Assert.Empty(Directory.GetDirectories(target));
    }

    [Theory]
    [InlineData("STATUS_ERROR", RestoreTerminalStatus.Error)]
    [InlineData("STATUS_USER_CANCELLED", RestoreTerminalStatus.Cancelled)]
    [InlineData("STATUS_SYSTEM_CANCELLED", RestoreTerminalStatus.Cancelled)]
    public async Task BackendTerminalWithoutSummary_CannotBecomeSuccess(string terminal, RestoreTerminalStatus expected)
    {
        using var workspace = new TempWorkspace("restore-terminal-truth");
        var target = workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "pre-existing is not proof of recovery");
        using var fixture = new ProtocolFixture(terminal);
        var result = await fixture.Restore(target, new[] { "keep.txt" });
        Assert.Equal(expected, result.Status);
        Assert.Equal(0, result.FilesRestored);
        Assert.NotNull(result.ErrorMessage);
        Assert.Equal("pre-existing is not proof of recovery", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public async Task SuccessWithoutAnyOutput_IsFailure_NotInventedCount()
    {
        using var workspace = new TempWorkspace("restore-empty-success");
        using var fixture = new ProtocolFixture("STATUS_SUCCESS");
        var result = await fixture.Restore(workspace.Sub("target"), new[] { "missing.txt" });
        Assert.Equal(RestoreTerminalStatus.Error, result.Status);
        Assert.Equal(0, result.FilesRestored);
    }

    [Fact]
    public async Task BackendErrorWithPartialStagedFile_DoesNotOverwriteOriginal()
    {
        using var workspace = new TempWorkspace("restore-partial-error");
        var target = workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "original intact");
        using var fixture = new ProtocolFixture("STATUS_ERROR", stage => File.WriteAllText(Path.Combine(stage, "keep.txt"), "partial"));
        var result = await fixture.Restore(target, new[] { "keep.txt" });
        Assert.Equal(RestoreTerminalStatus.Error, result.Status);
        Assert.Equal(0, result.FilesRestored);
        Assert.Equal("original intact", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public async Task NativeSummary_ProducesLiveProgress_AndPreservesBackendErrorAsWarningWhenAllOutputCompleted()
    {
        using var workspace = new TempWorkspace("restore-log-decoding");
        using var fixture = new ProtocolFixture("STATUS_ERROR", stage => File.WriteAllText(Path.Combine(stage, "keep.txt"), "complete"));
        fixture.Handler.SynchronousRequest = true;
        fixture.Handler.ProgressPolls = 2;
        fixture.Handler.NativeSummary = true;
        var updates = new List<RestoreProgressUpdate>();
        var result = await fixture.Restore(workspace.Sub("target"), new[] { "keep.txt" }, onProgress: updates.Add);
        Assert.Equal(RestoreTerminalStatus.Warning, result.Status);
        Assert.Equal(1, result.FilesRestored);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains(updates, p => p.BytesRestored == 4 && p.TotalBytes == 8);
    }

    [Fact]
    public async Task WarningWithoutSummary_CountsActualRecoveredFiles()
    {
        using var workspace = new TempWorkspace("restore-warning-count");
        using var fixture = new ProtocolFixture("STATUS_WARNING", stage => File.WriteAllText(Path.Combine(stage, "keep.txt"), "bytes"));
        var result = await fixture.Restore(workspace.Sub("target"), new[] { "keep.txt", "missing.txt" });
        Assert.Equal(RestoreTerminalStatus.Warning, result.Status);
        Assert.Equal(1, result.FilesRestored);
        Assert.Equal(2, result.TotalFiles);
        Assert.Equal(5, result.BytesRestored);
    }

    [Fact]
    public async Task CallerCancellation_SendsCancel_AndWaitsBeforeDeletingStaging()
    {
        using var workspace = new TempWorkspace("restore-cancel-ack");
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ProtocolFixture("STATUS_INPROGRESS", stage => File.WriteAllText(Path.Combine(stage, "keep.txt"), "partial"), cancellation);
        fixture.Handler.SynchronousRequest = true;
        var target = workspace.Sub("target");
        File.WriteAllText(Path.Combine(target, "keep.txt"), "original");
        var result = await fixture.Restore(target, new[] { "keep.txt" }, cancellation.Token);
        Assert.Equal(RestoreTerminalStatus.Cancelled, result.Status);
        Assert.Equal(1, fixture.Handler.CancelRequests);
        Assert.True(fixture.Handler.StagingExistedWhenCancelled);
        Assert.True(fixture.Handler.TerminalAcknowledged);
        Assert.Equal("original", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Empty(Directory.GetDirectories(target));
    }

    [Theory]
    [InlineData("/v1.Backrest/Backup", "STATUS_USER_CANCELLED")]
    [InlineData("/v1.Backrest/RunCommand", "STATUS_USER_CANCELLED")]
    public async Task SynchronousRepositoryRpc_CancelsDaemonBeforeReleasingOwnership(string endpoint, string expected)
    {
        using var cancel = new CancellationTokenSource();
        using var fixture = new ProtocolFixture("STATUS_INPROGRESS", cancel: cancel);
        fixture.Handler.SynchronousRequest = true;
        var terminal = await fixture.Track(endpoint, cancel.Token);
        Assert.Equal(expected, terminal.Status);
        Assert.Equal(1, fixture.Handler.CancelRequests);
        Assert.True(fixture.Handler.TerminalAcknowledged);
    }

    [Theory]
    [InlineData("STATUS_ERROR")]
    [InlineData("STATUS_WARNING")]
    public async Task SynchronousRepositoryRpc_UsesRecordedTerminalAfterHttpError(string status)
    {
        using var fixture = new ProtocolFixture(status);
        fixture.Handler.SynchronousRequest = true;
        var terminal = await fixture.Track("/v1.Backrest/RunCommand", default);
        Assert.Equal(status, terminal.Status);
        Assert.Equal("42", terminal.Id);
        Assert.NotEqual(BackupRunStatus.Success, ResticBackrestEngine.StatusFromBackrestTerminal(terminal.Status));
    }

    [Fact]
    public async Task RecoveredOperationPolling_DoesNotLeaveADeadlineThatCancelsHealthyWork()
    {
        using var fixture = new ProtocolFixture("STATUS_SUCCESS");
        fixture.Handler.SynchronousRequest = true;
        fixture.Handler.FailPolls = 1;
        fixture.Handler.ProgressPolls = 4;
        var terminal = await fixture.Track("/v1.Backrest/RunCommand", default, TimeSpan.FromMilliseconds(100));
        Assert.Equal("STATUS_SUCCESS", terminal.Status);
        Assert.Equal(0, fixture.Handler.CancelRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryFollowups_DrainBeforeReturn_WithoutTouchingOtherOperations(bool cancelFollowup)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ProtocolFixture("STATUS_SUCCESS", cancel: cancellation);
        fixture.Handler.SynchronousRequest = true;
        fixture.Handler.FollowupPolls = 3;
        fixture.Handler.CancelDuringFollowup = cancelFollowup;
        await fixture.Track("/v1.Backrest/Backup", cancellation.Token);
        Assert.True(fixture.Handler.FollowupAcknowledged);
        Assert.DoesNotContain("41", fixture.Handler.CancelledIds); // pre-existing repository job
        Assert.DoesNotContain("44", fixture.Handler.CancelledIds); // another restore
        if (cancelFollowup) Assert.Contains("43", fixture.Handler.CancelledIds);
        else Assert.Empty(fixture.Handler.CancelledIds);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("folder/../../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("file.txt\n/other.txt")]
    public void UnsafeSnapshotPaths_AreRejected(string relative)
        => Assert.Throws<InvalidOperationException>(() => ResticBackrestEngine.NormalizeRestoreRelativePath(relative));

    [Fact]
    public void LiveProgress_UsesBytesAndSkippedCounts()
    {
        Assert.True(ResticBackrestEngine.TryParseRestoreProgress(
            "{\"message_type\":\"status\",\"total_files\":8,\"files_restored\":2,\"files_skipped\":1,\"total_bytes\":100,\"bytes_restored\":40,\"percent_done\":0.4}", out var progress));
        Assert.Equal(8, progress.TotalFiles);
        Assert.Equal(3, progress.FilesRestored);
        Assert.Equal(40, progress.BytesRestored);
        Assert.False(ResticBackrestEngine.TryParseRestoreProgress("not a progress line", out _));
    }

    [Fact]
    public void RestoreCompletion_RejectsErroredLeaf_AndIntegrityCheckUsesCorrectWireEnum()
    {
        var complete = ResticBackrestEngine.ParseCompletedRestoreLeaves(
            "{\"message_type\":\"verbose_status\",\"action\":\"restored\",\"item\":\"/good.txt\"}\n" +
            "{\"message_type\":\"verbose_status\",\"action\":\"restored\",\"item\":\"/bad.txt\"}\n" +
            "{\"message_type\":\"error\",\"item\":\"/bad.txt\",\"error\":{\"message\":\"write failed\"}}\n");
        Assert.Contains("good.txt", complete);
        Assert.DoesNotContain("bad.txt", complete);
        Assert.Equal(3, (int)BackrestRepoTask.TaskCheck);
        Assert.Equal(1, (int)BackrestRepoTask.TaskIndexSnapshots);
    }

    private sealed class ProtocolFixture : IDisposable
    {
        public static string Source => Path.Combine(Path.GetTempPath(), "restore-fixture-source");
        public FakeBackrest Handler { get; }
        private readonly HttpClient _http;
        private readonly ResticBackrestEngine _engine;
        private readonly BackrestClient _client;
        public ProtocolFixture(string status, Action<string>? produceOutput = null, CancellationTokenSource? cancel = null)
        {
            Handler = new FakeBackrest(status, produceOutput, cancel);
            _http = new HttpClient(Handler) { BaseAddress = new Uri("http://127.0.0.1:1/") };
            var client = new BackrestClient(_http);
            _client = client;
            var sidecar = new BackrestSidecar("unused", "unused", "unused");
            typeof(BackrestSidecar).GetField("_proc", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sidecar, Process.GetCurrentProcess());
            typeof(BackrestSidecar).GetField("_client", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(sidecar, client);
            _engine = new ResticBackrestEngine(sidecar, new SecretsStore());
        }
        public Task<RestoreOpResult> Restore(string target, string[] paths, CancellationToken ct = default, Action<RestoreProgressUpdate>? onProgress = null, bool native = true)
        {
            Assert.True(native); // These are native Backrest protocol/staging tests.
            Handler.WholeSource = paths.Length > 1;
            return _engine.RestoreFilesAsync(new Backup(), Source,
                new Destination { Id = "restore-probe" }, "default", 1,
                Handler.WholeSource ? new[] { "/" } : paths, target, onProgress, ct, paths);
        }
        public Task<BackrestOperation> Track(string endpoint, CancellationToken ct, TimeSpan? uncertaintyTimeout = null) =>
            _engine.RunTrackedRepoRpcAsync(_client, "restore-probe-default", endpoint, new BackrestSidecar.EmptyRequest(),
                op => op.OperationBackup is not null || op.OperationCheck is not null || op.OperationForget is not null || op.OperationRunCommand is not null, ct,
                uncertaintyTimeout: uncertaintyTimeout);
        public Task<IReadOnlyList<RevisionFile>> ListFiles() =>
            _engine.ListSnapshotFilesAsync(new Backup(), Source, new Destination { Id = "restore-probe" }, "default", 1, TestContext.Current.CancellationToken);
        public Task<IReadOnlyList<RevisionDirEntry>?> ListDirectory() =>
            _engine.ListSnapshotDirectoryAsync(new Backup(), Source, new Destination { Id = "restore-probe" }, "default", 1, "", TestContext.Current.CancellationToken);
        public void Dispose() => _http.Dispose();
    }

    private sealed class FakeBackrest(string status, Action<string>? produceOutput, CancellationTokenSource? cancel) : HttpMessageHandler
    {
        public int CancelRequests { get; private set; }
        public bool StagingExistedWhenCancelled { get; private set; }
        public bool TerminalAcknowledged { get; private set; }
        public bool SynchronousRequest { get; set; }
        public int ProgressPolls { get; set; }
        public bool NativeSummary { get; set; }
        public bool WholeSource { get; set; }
        public int FailPolls { get; set; }
        public int FollowupPolls { get; set; }
        public bool CancelDuringFollowup { get; set; }
        public bool FollowupAcknowledged { get; private set; }
        public List<string> CancelledIds { get; } = new();
        public List<string> ListedPaths { get; } = new();
        public bool NestedListing { get; set; }
        public int DiscoveryDelayPolls { get; set; }
        public bool IncludeOtherMatchingOperation { get; set; }
        public bool DelayAcknowledgement { get; set; }
        public bool DelayNativeAcknowledgement { get; set; }
        public bool AcknowledgementReleased => _acknowledgement.Task.IsCompleted;
        public Task NativeSubmitted => _nativeSubmitted.Task;
        public void ReleaseAcknowledgement() => _acknowledgement.TrySetResult();
        public string NativePath => _nativePath ?? "";
        private string? _endpoint;
        public string? LastEndpoint => _endpoint;
        private string? _nativeTarget;
        private string? _nativePath;
        private int _polls;
        private int _followupPolls;
        private int _acknowledgementPolls;
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _nativeSubmitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string response;
            switch (path)
            {
                case "/v1.Backrest/ListSnapshots": response = "{\"snapshots\":[{\"id\":\"snapshot\",\"unixTimeMs\":1}]}"; break;
                case "/v1.Backrest/GetConfig": response = "{\"repos\":[{\"id\":\"restore-probe-default\",\"guid\":\"guid\"}]}"; break;
                case "/v1.Backrest/ListSnapshotFiles":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        Assert.Equal("restore-probe-default", body.RootElement.GetProperty("repo_id").GetString());
                        Assert.False(body.RootElement.TryGetProperty("repo_guid", out _));
                        var listed = body.RootElement.GetProperty("path").GetString()!;
                        ListedPaths.Add(listed);
                        var prefix = "/" + ResticBackrestEngine.SourcePathToSnapshotPrefix(ProtocolFixture.Source);
                        var entries = new List<object> { new { path = NestedListing ? listed : prefix, type = "dir" } };
                        if (listed == prefix)
                        {
                            entries.Add(new { path = prefix + "/kept.txt", type = "file", size = "7" });
                            if (NestedListing)
                            {
                                entries.Add(new { path = prefix + "/nested", type = "dir" });
                                entries.Add(new { path = prefix + "-old/unrelated.txt", type = "file", size = "99" });
                            }
                        }
                        else if (NestedListing && listed == prefix + "/nested")
                        {
                            entries.Add(new { path = listed + "/café [1].txt", type = "file", size = "12" });
                            entries.Add(new { path = listed + "/empty", type = "dir" });
                        }
                        response = JsonSerializer.Serialize(new { path = listed, entries });
                    }
                    break;
                case "/v1.Backrest/Restore":
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        _nativeTarget = body.RootElement.GetProperty("target").GetString()!;
                        _nativePath = body.RootElement.GetProperty("path").GetString()!;
                        Assert.False(Directory.Exists(_nativeTarget));
                        Directory.CreateDirectory(_nativeTarget);
                        var output = WholeSource ? Path.Combine(_nativeTarget, "restore-fixture-source") : _nativeTarget;
                        Directory.CreateDirectory(output);
                        produceOutput?.Invoke(output);
                        _endpoint = path;
                    }
                    _nativeSubmitted.TrySetResult();
                    if (DelayNativeAcknowledgement) await _acknowledgement.Task.WaitAsync(cancellationToken);
                    response = "{\"operationId\":\"42\"}"; // Backrest 1.14.1 returns the scheduled id.
                    break;
                case "/v1.Backrest/Backup":
                case "/v1.Backrest/Forget":
                case "/v1.Backrest/DoRepoTask":
                case "/v1.Backrest/RunCommand":
                    _endpoint = path;
                    if (DelayAcknowledgement) await _acknowledgement.Task.WaitAsync(cancellationToken);
                    if (SynchronousRequest)
                    {
                        await _completed.Task.WaitAsync(cancellationToken);
                        if (status != "STATUS_SUCCESS") return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        { Content = new StringContent("{\"code\":\"unknown\",\"message\":\"fixture operation failed\"}") };
                    }
                    response = path == "/v1.Backrest/Backup" ? "{}" : "{\"operationId\":\"42\"}";
                    break;
                case "/v1.Backrest/GetOperations":
                    if (_endpoint is null) { response = "{\"operations\":[{\"id\":\"41\",\"status\":\"STATUS_SUCCESS\",\"operationCheck\":{}}]}"; break; }
                    if (DelayAcknowledgement && ++_acknowledgementPolls >= 2) _acknowledgement.TrySetResult();
                    if (FailPolls-- > 0) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                        { Content = new StringContent("{\"code\":\"unavailable\",\"message\":\"temporary fixture fault\"}") };
                    var recorded = new List<object>();
                    if (IncludeOtherMatchingOperation)
                        recorded.Add(new { id = "99", status = "STATUS_ERROR", operationCheck = new object(),
                            operationRestore = _nativeTarget is null ? null : new { target = _nativeTarget, path = _nativePath } });
                    if (DiscoveryDelayPolls > 0)
                    {
                        DiscoveryDelayPolls--;
                        response = JsonSerializer.Serialize(new { operations = recorded });
                        break;
                    }
                    var current = CancelRequests > 0 ? "STATUS_USER_CANCELLED" : _polls++ < ProgressPolls ? "STATUS_INPROGRESS" : status;
                    if (CancelRequests > 0) TerminalAcknowledged = true;
                    else if (current == "STATUS_INPROGRESS") cancel?.Cancel();
                    if (current != "STATUS_INPROGRESS") _completed.TrySetResult();
                    recorded.Add(new { id = "42", status = current, displayMessage = "Fixture terminal message",
                        operationRestore = _nativeTarget is null ? null : new { target = _nativeTarget, path = _nativePath,
                            lastStatus = new { messageType = NativeSummary && current != "STATUS_INPROGRESS" ? "summary" : "status", totalBytes = "8", bytesRestored = NativeSummary && current != "STATUS_INPROGRESS" ? "8" : "4", totalFiles = "1", filesRestored = NativeSummary && current != "STATUS_INPROGRESS" ? "1" : "0", percentDone = NativeSummary && current != "STATUS_INPROGRESS" ? 1.0 : 0.5 } },
                        operationBackup = _endpoint == "/v1.Backrest/Backup" ? new object() : null,
                        operationCheck = _endpoint == "/v1.Backrest/DoRepoTask" ? new object() : null,
                        operationForget = _endpoint == "/v1.Backrest/Forget" ? new object() : null,
                        operationRunCommand = _endpoint == "/v1.Backrest/RunCommand" ? new { command = "fixture command" } : null });
                    if (FollowupPolls > 0)
                    {
                        var followup = CancelledIds.Contains("43") ? "STATUS_USER_CANCELLED" :
                            _followupPolls++ < FollowupPolls ? "STATUS_INPROGRESS" : "STATUS_SUCCESS";
                        FollowupAcknowledged = followup != "STATUS_INPROGRESS";
                        recorded.Add(new { id = "43", status = followup, operationCheck = new object() });
                        recorded.Add(new { id = "41", status = "STATUS_INPROGRESS", operationCheck = new object() });
                        recorded.Add(new { id = "44", status = "STATUS_INPROGRESS", operationRunCommand = new { command = "unrelated restore" } });
                        if (CancelDuringFollowup && followup == "STATUS_INPROGRESS") cancel?.Cancel();
                    }
                    response = JsonSerializer.Serialize(new { operations = recorded });
                    break;
                case "/v1.Backrest/Cancel":
                    CancelRequests++;
                    using (var cancelBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                    {
                        Assert.False(cancelBody.RootElement.TryGetProperty("value", out _));
                        CancelledIds.Add(cancelBody.RootElement.GetProperty("operationId").GetString()!);
                    }
                    StagingExistedWhenCancelled = Directory.Exists(_nativeTarget);
                    response = "{}";
                    break;
                default: throw new InvalidOperationException("Unexpected request: " + path);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
