using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class TestRestoreJournalTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Starting_while_ownership_is_locked_waits_without_writing_and_can_be_stopped(bool cancel)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scratch = new TempWorkspace();
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        var config = new ConfigStore(); config.Load();
        var backup = new Backup { Name = "Ownership wait" };
        var root = Path.Combine(scratch.ConfigRoot, "test-restores");
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, TestRestoreContext.Hash(backup.Id) + ".lock");
        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var service = new TestRestoreService(config, ServiceLocator.Verifier);
        var session = service.Start(backup, new());
        try
        {
            Assert.Same(session, service.Start(backup, new()));
            await Task.Delay(6500, TestContext.Current.CancellationToken);
            Assert.False(session.Completion.IsCompleted);
            Assert.True(service.IsRunning(backup.Id));
            Assert.Empty(Directory.GetFiles(root, "*.json"));
            var notice = Assert.Single(FileAccessRetry.Snapshot(), n => n.Path == lockPath);
            Assert.Equal("Waiting to start Test restore", notice.Title);
            if (cancel) notice.StopCommand.Execute(null); else held.Dispose();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.False(service.IsRunning(backup.Id));
            Assert.DoesNotContain(FileAccessRetry.Snapshot(), n => n.Path == lockPath);
            if (cancel)
            {
                Assert.Equal(RestoreTerminalStatus.Cancelled, session.Status);
                Assert.Empty(Directory.GetFiles(root, "*.json"));
            }
            else Assert.NotEmpty(Directory.GetFiles(root, "*.json"));
        }
        finally
        {
            held.Dispose(); session.Cancel();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            await ServiceLocator.ActiveRestores.DismissAsync(session);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Atomic_progress_save_waits_for_unlock_or_cancellation_and_preserves_the_old_record(bool persistent)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scratch = new TempWorkspace();
        var path = Path.Combine(scratch.Root, "progress.json");
        TestRestoreContext.WriteAtomic(path, new { Summary = "Previous confirmed result" });
        var previous = File.ReadAllBytes(path);
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stop = new CancellationTokenSource();
        var save = Task.Run(() => TestRestoreContext.WriteAtomic(path, new { Summary = "New confirmed result" }, stop.Token));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(save.IsCompleted, "A held replacement lock must not be treated as a completed save.");
        Assert.Equal(previous, File.ReadAllBytes(path));
        if (persistent)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.Equal(previous, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(scratch.Root, "*.tmp"));
        }
        else
        {
            held.Dispose();
            await save.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal("New confirmed result", JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("Summary").GetString());
            Assert.Empty(Directory.GetFiles(scratch.Root, "*.tmp"));
        }
    }

    [Fact]
    public void Sampling_prioritizes_never_checked_then_oldest_and_respects_byte_target()
    {
        var files = Enumerable.Range(0, 100).Select(i => new RevisionFile($"folder-{i % 7}/file-{i}", 100, DateTime.UnixEpoch)).ToArray();
        DateTime Checked(RevisionFile f) => f.Path.EndsWith("file-99") ? DateTime.MinValue : DateTime.UnixEpoch.AddDays(int.Parse(f.Path.Split('-').Last()));
        var picked = TestRestoreContext.SelectSample(files, 5, long.MaxValue, Checked);
        Assert.Equal(5, picked.Count);
        Assert.Equal(files[99], picked[0]);
        Assert.Equal(files.Take(4), picked.Skip(1));
        Assert.DoesNotContain(files[5], picked);
        Assert.Equal(2, TestRestoreContext.SelectSample(files, 50, 150, Checked).Count);
    }

    [Fact]
    public void A_whole_large_file_is_not_silently_excluded_and_empty_files_can_be_sampled()
    {
        var huge = new RevisionFile("large", 5_000_000_000, DateTime.UnixEpoch);
        Assert.Equal(huge, Assert.Single(TestRestoreContext.SelectSample(new[] { huge }, 1, 100, _ => DateTime.MinValue)));
        var empty = Enumerable.Range(0, 100).Select(i => new RevisionFile("empty-" + i, 0, DateTime.UnixEpoch)).ToArray();
        Assert.Equal(5, TestRestoreContext.SelectSample(empty, 5, 100, _ => DateTime.MinValue).Count);
        Assert.Empty(TestRestoreContext.SelectSample(Array.Empty<RevisionFile>(), 1, 100, _ => DateTime.MinValue));
    }

    [Fact]
    public void Changed_file_metadata_gets_a_new_sampling_identity()
    {
        var file = new RevisionFile("same-name", 100, DateTime.UnixEpoch);
        Assert.NotEqual(TestRestoreContext.FileKey("cell", file), TestRestoreContext.FileKey("cell", file with { SizeBytes = 101 }));
        Assert.NotEqual(TestRestoreContext.FileKey("cell", file), TestRestoreContext.FileKey("cell", file with { ModifiedUtc = DateTime.UnixEpoch.AddDays(1) }));
        Assert.NotEqual(TestRestoreContext.FileKey("cell", file), TestRestoreContext.FileKey("other-destination", file));
    }

    [Fact]
    public void Batches_limit_both_file_count_and_temporary_bytes()
    {
        var files = Enumerable.Range(0, 300).Select(i => new RevisionFile("file-" + i, 1, DateTime.UnixEpoch)).ToArray();
        Assert.Equal(new[] { 128, 128, 44 }, BackupVerifier.VerificationBatches(files).Select(b => b.Length));
        var large = new[] { new RevisionFile("a", 200_000_000, DateTime.UnixEpoch), new RevisionFile("b", 200_000_000, DateTime.UnixEpoch), new RevisionFile("c", 1_000_000_000, DateTime.UnixEpoch) };
        Assert.Equal(new[] { 1, 1, 1 }, BackupVerifier.VerificationBatches(large).Select(b => b.Length));
    }

    [Fact]
    public void Journal_round_trip_keeps_exact_snapshot_sample_and_completed_file_results()
    {
        using var scratch = new TempWorkspace("verify-journal");
        var path = Path.Combine(scratch.Sub("records"), "saved.json");
        var journal = new TestRestoreJournal { BackupId = "backup", Cells = { new TestRestoreCell { Key = "source-and-destination", Snapshot = new RevisionSummary(4, DateTime.UnixEpoch, 2, 12) { SnapshotId = "immutable-old-snapshot" },
            Files = { new RevisionFile("one", 6, DateTime.UnixEpoch), new RevisionFile("two", 6, DateTime.UnixEpoch) },
            Results = { new FileVerifyResult("one", 6, 6, FileVerifyStatus.OK, null) } } } };
        TestRestoreContext.WriteAtomic(path, journal);
        var saved = JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(path))!;
        Assert.Equal("running", saved.State);
        Assert.Equal("immutable-old-snapshot", saved.Cells.Single().Snapshot.SnapshotId);
        Assert.Equal(2, saved.Cells.Single().Files.Count);
        Assert.Equal("one", Assert.Single(saved.Cells.Single().Results).Path);
        Assert.False(saved.Cells.Single().Complete);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(101)] [InlineData(double.NaN)]
    public void Invalid_sample_percentages_cannot_start(double percent) => Assert.Throws<ArgumentException>(() => new TestRestoreOptions(SamplePercent: percent).Validate());

    [Theory]
    [InlineData("cancelled", "completed")]
    [InlineData("completed", "cancelled")]
    public void The_first_durable_terminal_result_wins(string first, string later)
    {
        using var scratch = new TempWorkspace();
        var path = Path.Combine(scratch.Root, "record.json");
        var journal = new TestRestoreJournal();
        var context = new TestRestoreContext(journal, path, Session(), Path.Combine(scratch.Root, "history.json"));
        context.Save();
        Assert.True(context.Finish(first, "First result"));
        Assert.False(context.Finish(later, "Must not replace the first result"));
        var saved = JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(path))!;
        Assert.Equal(first, saved.State);
        Assert.Equal("First result", saved.Summary);
    }

    [Fact]
    public void Failed_persistence_does_not_claim_a_stop_or_dismissal_was_saved()
    {
        using var scratch = new TempWorkspace();
        var path = scratch.Sub("directory-instead-of-journal");
        var journal = new TestRestoreJournal();
        var context = new TestRestoreContext(journal, path, Session(), Path.Combine(scratch.Root, "history.json"));
        Assert.True(Record.Exception(() => context.Finish("cancelled", "Stopped")) is IOException or UnauthorizedAccessException);
        Assert.Equal("running", journal.State);
        Assert.Null(journal.EndedUtc);
        Assert.True(Record.Exception(() => context.Dismiss()) is IOException or UnauthorizedAccessException);
        Assert.False(journal.Dismissed);
    }

    [AvaloniaFact]
    public async Task Changed_destinations_and_corrupt_progress_are_preserved_and_reported_without_resume()
    {
        using var scratch = new TempWorkspace();
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        var config = new ConfigStore();
        config.Load();
        var destination = new Destination { PathOrSubpath = scratch.Sub("repository-one") };
        var backup = new Backup { Name = "Recovery protection", SourcePaths = { scratch.Sub("source") },
            Targets = { new BackupTarget { DestinationId = destination.Id } } };
        config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
        var journal = new TestRestoreJournal { BackupId = backup.Id, BackupName = backup.Name,
            RouteFingerprint = TestRestoreContext.Fingerprint(backup, config) };
        var root = Path.Combine(scratch.ConfigRoot, "test-restores");
        var path = Path.Combine(root, journal.Id + ".json");
        TestRestoreContext.WriteAtomic(path, journal);
        var broken = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(broken, "{unfinished progress");
        config.Update(c => c.Destinations.Single().PathOrSubpath = scratch.Sub("repository-two"));
        var before = ServiceLocator.ActiveRestores.Sessions.ToArray();
        try
        {
            var service = new TestRestoreService(config, ServiceLocator.Verifier);
            await service.ResumeInterruptedAsync(); Dispatcher.UIThread.RunJobs();
            var sessions = ServiceLocator.ActiveRestores.Sessions.Except(before).ToArray();
            Assert.Equal(2, sessions.Length);
            Assert.All(sessions, s => Assert.Equal(RestoreTerminalStatus.Error, s.Status));
            var changed = sessions.Single(s => s.BackupId == backup.Id);
            Assert.Contains("settings changed", changed.ErrorMessage);
            Assert.False(service.IsRunning(backup.Id));
            Assert.Equal("{unfinished progress", File.ReadAllText(broken));
            Assert.Equal("failed", JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(path))!.State);
            await service.ResumeInterruptedAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, ServiceLocator.ActiveRestores.Sessions.Except(before).Count());
            await ServiceLocator.ActiveRestores.DismissAsync(changed);
            Assert.True(JsonSerializer.Deserialize<TestRestoreJournal>(File.ReadAllText(path))!.Dismissed);
            await new TestRestoreService(config, ServiceLocator.Verifier).ResumeInterruptedAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(ServiceLocator.ActiveRestores.Sessions.Except(before), s => s.BackupId == backup.Id);
        }
        finally
        {
            foreach (var session in ServiceLocator.ActiveRestores.Sessions.Except(before).ToArray())
                await ServiceLocator.ActiveRestores.DismissAsync(session);
        }
    }

    private static ActiveRestoreSession Session() => new()
    {
        Id = "test", BackupId = "backup", BackupName = "Test", DestinationName = "Test", TargetPath = "temporary",
    };
}
