using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U179 asked the engine (U175 step 1) to read each check entry's data limit:
/// a data sample reads 1% of the stored data up to its own limit (1 GiB when
/// it has none), and a full data read reads all of it with
/// <c>check --read-data</c>, recorded as <see cref="CheckDepth.AllData"/>.
/// </summary>
public sealed class DataCheckLimitTests(ITestOutputHelper output)
{
    private const long MiB = 1024 * 1024;

    [Theory]
    [InlineData(500 * MiB, null, 5 * MiB)]                 // 1% of a small repository
    [InlineData(500L * 1024 * MiB, null, BackupCheck.OneGiB)] // 1%, at most 1 GiB without a limit
    [InlineData(500L * 1024 * MiB, 200 * MiB, 200 * MiB)]  // at most the entry's limit
    [InlineData(500 * MiB, 200 * MiB, 5 * MiB)]           // 1% under the limit
    [InlineData(10L * 1024 * BackupCheck.OneGiB, 5 * BackupCheck.OneGiB, 5 * BackupCheck.OneGiB)] // a limit above 1 GiB is kept
    [InlineData(50L, 4096L, 1L)]                           // never less than one byte
    public void A_data_sample_reads_one_percent_up_to_its_limit(long stored, long? limit, long expected)
        => Assert.Equal(expected, ResticBackrestEngine.DataCheckSampleBytes(stored, limit));

    [Fact]
    public void A_limit_below_one_byte_is_refused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ResticBackrestEngine.DataCheckSampleBytes(1000, 0));

    [Fact]
    public void Each_check_entry_becomes_its_own_command_and_depth()
    {
        var backup = new Backup { Name = "Docs", BandwidthLimitKBps = 512 };
        var structure = BackupOrchestrator.EngineViewFor(backup, new BackupCheck { Kind = CheckKind.Structure });
        var sample = BackupOrchestrator.EngineViewFor(backup, new BackupCheck { Kind = CheckKind.DataSample, MaximumBytes = 200 * MiB });
        var full = BackupOrchestrator.EngineViewFor(backup, new BackupCheck { Kind = CheckKind.FullDataRead });

        Assert.False(ResticBackrestEngine.ReadsAllData(structure));
        Assert.False(ResticBackrestEngine.ReadsAllData(sample));
        Assert.True(ResticBackrestEngine.ReadsAllData(full));
        Assert.Equal("check --read-data-subset=4096B --limit-download=512", ResticBackrestEngine.DataCheckCommand(sample, 4096));
        Assert.Equal("check --read-data --limit-download=512", ResticBackrestEngine.DataCheckCommand(full, null));
        backup.BandwidthLimitKBps = 0;
        Assert.Equal("check --read-data", ResticBackrestEngine.DataCheckCommand(BackupOrchestrator.EngineViewFor(backup,
            new BackupCheck { Kind = CheckKind.FullDataRead }), null));

        Assert.Contains("were not downloaded", ResticBackrestEngine.RepositoryCheckDescription(structure));
        Assert.Contains("up to 200 MiB", ResticBackrestEngine.RepositoryCheckDescription(sample));
        Assert.Contains("not a full-data check", ResticBackrestEngine.RepositoryCheckDescription(sample));
        Assert.Contains("every piece of saved data", ResticBackrestEngine.RepositoryCheckDescription(full));
        Assert.DoesNotContain("not a full-data check", ResticBackrestEngine.RepositoryCheckDescription(full));
        // A copy without a limit (settings from before U179) keeps 1 GiB.
        Assert.Contains("up to 1 GiB", ResticBackrestEngine.RepositoryCheckDescription(new Backup { CheckReadDataSample = true }));
        Assert.True(new ResticBackrestEngine(new SecretsStore()).HonoursDataCheckLimit);
    }

    /// <summary>The real engine, through the stand-alone check (restic run
    /// directly, U175 step 2): a full data read runs
    /// <c>check --read-data</c> and is recorded as reading all data; a data
    /// sample with a small limit reads no more than that limit.</summary>
    [AvaloniaFact]
    public async Task The_real_engine_reads_all_data_or_a_sample_within_the_limit()
    {
        Assert.True(RealEngineFixture.BinariesPresent, RealEngineFixture.MissingBinariesReason());
        using var workspace = new TempWorkspace("data-check-limit");
        var secrets = new SecretsStore();
        var source = workspace.Sub("source");
        for (var i = 0; i < 30; i++) File.WriteAllBytes(Path.Combine(source, $"file{i:00}.bin"), RandomNumberGenerator.GetBytes(256 * 1024));
        var destination = new Destination { Name = "Local data check", Kind = DestinationKind.LocalFolder, PathOrSubpath = workspace.Sub("repo"),
            Encrypted = true, StoragePasswordRef = secrets.PutNew("data-check", Guid.NewGuid().ToString("N")) };
        var backup = new Backup { Name = "Data check", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true,
            PruneSchedule = new BackupSchedule(), Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
        var engine = new ResticBackrestEngine(secrets);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        Assert.Equal(BackupRunStatus.Success, (await engine.BackupPruneCheckAsync(backup, source, destination, "default", deadline.Token)).Status);

        var fullLines = new ConcurrentQueue<string>();
        var full = await engine.RunCheckOnlyAsync(BackupOrchestrator.EngineViewFor(backup, new BackupCheck { Kind = CheckKind.FullDataRead }),
            source, destination, "default", deadline.Token, fullLines.Enqueue);
        output.WriteLine(string.Join("\n", fullLines));
        Assert.True(full.Status == BackupRunStatus.Success, full.Summary + " " + string.Join(" | ", full.Errors));
        Assert.Equal(CheckDepth.AllData, Assert.Single(full.Checks).Depth);
        Assert.Contains(fullLines, l => l.Contains("check --read-data", StringComparison.Ordinal));
        Assert.DoesNotContain(fullLines, l => l.Contains("--read-data-subset", StringComparison.Ordinal));
        Assert.Contains(fullLines, l => l.StartsWith("[backspace] Data check reads all stored data", StringComparison.Ordinal));
        Assert.Contains("every piece of saved data", full.Summary);

        var sampleLines = new ConcurrentQueue<string>();
        var sample = await engine.RunCheckOnlyAsync(BackupOrchestrator.EngineViewFor(backup, new BackupCheck { Kind = CheckKind.DataSample, MaximumBytes = 4096 }),
            source, destination, "default", deadline.Token, sampleLines.Enqueue);
        output.WriteLine(string.Join("\n", sampleLines));
        Assert.True(sample.Status == BackupRunStatus.Success, sample.Summary + " " + string.Join(" | ", sample.Errors));
        Assert.Equal(CheckDepth.SavedDataSample, Assert.Single(sample.Checks).Depth);
        // 1% of about 7.5 MB is 75 KB: the 4 KiB limit is what is read.
        var target = sampleLines.Select(l => Regex.Match(l, @"^\[backspace\] Data check targets (.+?) bytes")).Single(m => m.Success);
        Assert.Equal("4096", new string(target.Groups[1].Value.Where(char.IsDigit).ToArray()));
        Assert.Contains(sampleLines, l => l.Contains("check --read-data-subset=4096B", StringComparison.Ordinal));
    }
}
