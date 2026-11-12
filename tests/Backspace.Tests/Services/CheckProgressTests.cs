using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U197 and U198 (11 November 2026): a running check says which
/// check runs in short, its step and how far it got, from restic's own
/// counts.</summary>
public sealed class CheckProgressTests(ITestOutputHelper output)
{
    // restic 0.19's output for check --read-data with RESTIC_PROGRESS_FPS=1,
    // piped (recorded on 11 November 2026).
    private static readonly string[] DataCheck =
    [
        "using temporary cache in C:\\Temp\\restic-check-cache-1", "create exclusive lock for repository",
        "load indexes", "check all packs", "check snapshots, trees and blobs", "[0:00] 100.00%  1 / 1 snapshots",
        "read all data", "[0:01] 33.33%  1 / 3 packs", "[0:02] 33.33%  1 / 3 packs", "[0:05] 66.67%  2 / 3 packs",
        "[0:06] 100.00%  3 / 3 packs", "no errors were found",
    ];

    [Fact]
    public void A_data_check_moves_forward_through_its_steps()
    {
        var reader = new CheckProgressReader(readsData: true);
        var seen = new List<(string Step, double Percent)>();
        foreach (var line in DataCheck)
            if (reader.Read(line)) seen.Add((reader.Step.Text, reader.Percent));
        foreach (var s in seen) output.WriteLine($"{s.Percent,6:0.0}%  {s.Step}");
        Assert.Contains(seen, s => s.Step == "Loading the index");
        Assert.Contains(seen, s => s.Step == "Checking the snapshots · 1 of 1 snapshots");
        Assert.Contains(seen, s => s.Step == "Reading all stored data · 1 of 3 packs");
        Assert.Contains(seen, s => s.Step == "Reading all stored data · 2 of 3 packs");
        // Never backwards, and the bar is full when restic says it is done.
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second.Percent >= p.First.Percent));
        Assert.Equal(100, seen[^1].Percent);
        // Reading the data is most of the bar: a third of the packs is past 50%.
        Assert.InRange(seen.First(s => s.Step.EndsWith("1 of 3 packs", StringComparison.Ordinal)).Percent, 50, 60);
    }

    [Fact]
    public void A_structure_check_counts_its_snapshots_and_ignores_other_lines()
    {
        var reader = new CheckProgressReader(readsData: false);
        Assert.False(reader.Read("using temporary cache in C:\\Temp"));
        Assert.False(reader.Read("create exclusive lock for repository"));
        Assert.True(reader.Read("load indexes"));
        Assert.Equal(0, reader.Percent);
        Assert.True(reader.Read("check snapshots, trees and blobs"));
        Assert.True(reader.Read("[0:10] 50.00%  20 / 40 snapshots"));
        Assert.Equal("Checking the snapshots · 20 of 40 snapshots", reader.Step.Text);
        Assert.Equal(60, reader.Percent, 3);
        Assert.False(reader.Read("some other line 5 / 6"));
        Assert.True(CheckProgressReader.IsProgressLine("[1:02:03] 12.50%  1 / 8 packs"));
        Assert.False(CheckProgressReader.IsProgressLine("read 20000000 bytes (66.7%) of data packs"));
    }

    [Theory]
    [InlineData("Data check (1 GiB sample) (1 of 2)", "Data check (1 of 2)")]
    [InlineData("Data check (500 MiB sample)", "Data check")]
    [InlineData("Structure check", "Structure check")]
    [InlineData("Restore check (1% of files, at most 1 GiB) (2 of 2)", "Restore check (2 of 2)")]
    [InlineData("Restore check (0.5% of files)", "Restore check")]
    [InlineData("Data check (all data) (1 of 3)", "Data check (all data) (1 of 3)")]
    public void The_card_names_the_check_in_short(string activity, string shown)
        => Assert.Equal(shown, BackupCardViewModel.ShortCheckName(activity));

    /// <summary>U215: the owner's data sample check failed because a test
    /// restore was reading the same repository (restic's live lock, exit code
    /// 11). A check now waits for the other operation and then runs; the card
    /// says it is waiting. Here a real restic holds a shared lock while the
    /// check starts, then lets go.</summary>
    [AvaloniaFact]
    public async Task A_check_waits_for_another_operation_using_the_repository()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace("check-waits");
        var secrets = new SecretsStore();
        var source = workspace.Sub("source");
        File.WriteAllBytes(Path.Combine(source, "a.bin"), RandomNumberGenerator.GetBytes(64 * 1024));
        var password = Guid.NewGuid().ToString("N");
        var destination = new Destination { Name = "Local check", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("repo"),
            Encrypted = true, StoragePasswordRef = secrets.PutNew("check-waits", password) };
        var backup = new Backup { Name = "Check waits", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        var engine = new ResticEngine(secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Assert.Equal(BackupRunStatus.Success, (await engine.BackupPruneCheckAsync(backup, source, destination, "default", deadline.Token)).Status);

        // Another operation reads the repository: restic waiting on its input, its lock live.
        var start = new System.Diagnostics.ProcessStartInfo(AppPaths.ResticBinary) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["RESTIC_PASSWORD"] = password;
        foreach (var arg in new[] { "--repo", destination.PathOrSubpath!, "backup", "--stdin", "--stdin-filename", "held.txt", "--quiet" }) start.ArgumentList.Add(arg);
        using var held = System.Diagnostics.Process.Start(start)!;
        var locks = Path.Combine(destination.PathOrSubpath!, "locks");
        for (var i = 0; i < 200 && !Directory.EnumerateFiles(locks).Any(); i++) await Task.Delay(50, deadline.Token);
        Assert.NotEmpty(Directory.EnumerateFiles(locks));

        var previous = ResticEngine.LockWaitInterval;
        ResticEngine.LockWaitInterval = TimeSpan.FromSeconds(1);
        try
        {
            var steps = new ConcurrentQueue<string>();
            var lines = new ConcurrentQueue<string>();
            var check = engine.RunCheckOnlyAsync(BackupOrchestrator.EngineViewFor(backup, new BackupCheck { Kind = CheckKind.Structure }),
                source, destination, "default", deadline.Token, lines.Enqueue, (step, _) => steps.Enqueue(step.Text));
            for (var i = 0; i < 200 && !steps.Contains("Waiting: another operation is using the repository"); i++) await Task.Delay(50, deadline.Token);
            Assert.Contains("Waiting: another operation is using the repository", steps);
            Assert.False(check.IsCompleted);
            // The other operation ends: the check runs and passes.
            held.StandardInput.Close();
            await held.WaitForExitAsync(deadline.Token);
            var result = await check;
            foreach (var l in lines) output.WriteLine(l);
            Assert.True(result.Status == BackupRunStatus.Success, result.Summary + " " + string.Join(" | ", result.Errors));
            Assert.Contains(lines, l => l.Contains("The repository is in use by another operation", StringComparison.Ordinal));
        }
        finally
        {
            ResticEngine.LockWaitInterval = previous;
            if (!held.HasExited) held.Kill(true);
        }
    }

    /// <summary>The real engine: restic's counts reach the card's progress
    /// once a second, and the run's log keeps only the finished counts.</summary>
    [AvaloniaFact]
    public async Task The_real_engine_reports_a_checks_progress()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace("check-progress");
        var secrets = new SecretsStore();
        var source = workspace.Sub("source");
        for (var i = 0; i < 40; i++) File.WriteAllBytes(Path.Combine(source, $"file{i:00}.bin"), RandomNumberGenerator.GetBytes(1024 * 1024));
        var destination = new Destination { Name = "Local check", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("repo"),
            Encrypted = true, StoragePasswordRef = secrets.PutNew("check-progress", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Check progress", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        var engine = new ResticEngine(secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Assert.Equal(BackupRunStatus.Success, (await engine.BackupPruneCheckAsync(backup, source, destination, "default", deadline.Token)).Status);

        // Reading slowly (8 MB/s), so restic reports packs part-way.
        backup.BandwidthLimitKBps = 8000;
        var view = BackupOrchestrator.EngineViewFor(backup, new BackupCheck { Kind = CheckKind.FullDataRead });
        var lines = new ConcurrentQueue<string>();
        var steps = new ConcurrentQueue<(string Step, double Percent)>();
        var result = await engine.RunCheckOnlyAsync(view, source, destination, "default", deadline.Token, lines.Enqueue,
            (step, percent) => steps.Enqueue((step.Text, percent)));
        foreach (var s in steps) output.WriteLine($"{s.Percent,6:0.0}%  {s.Step}");
        Assert.True(result.Status == BackupRunStatus.Success, result.Summary + " " + string.Join(" | ", result.Errors));
        var seen = steps.ToList();
        Assert.Contains(seen, s => s.Step == "Loading the index");
        Assert.Contains(seen, s => s.Step.StartsWith("Checking the snapshots · 1 of 1", StringComparison.Ordinal));
        // Part-way through the packs, then all of them.
        Assert.Contains(seen, s => System.Text.RegularExpressions.Regex.IsMatch(s.Step, @"^Reading all stored data · (\d+) of (\d+) packs$")
            && s.Percent is > 30 and < 100);
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second.Percent >= p.First.Percent), "The bar went backwards.");
        Assert.Equal(100, seen[^1].Percent);
        // The log: the finished counts only, not one line a second.
        var bars = lines.Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\[[0-9:]+\]\s+[0-9.]+%\s+\d+ / \d+")).ToList();
        foreach (var bar in bars) output.WriteLine("log: " + bar);
        Assert.NotEmpty(bars);
        Assert.All(bars, b => Assert.Contains("100.00%", b));
    }
}
