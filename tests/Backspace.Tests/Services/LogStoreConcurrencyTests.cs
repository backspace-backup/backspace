using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class LogStoreConcurrencyTests
{
    [Fact]
    public async Task Independent_history_writers_preserve_all_runs_and_stale_reader_sees_them()
    {
        using var workspace = new TempWorkspace();
        var reader = new LogStore();
        new LogStore().AppendRun(Run(0));
        Assert.Single(reader.LoadHistory("shared"));
        Assert.Single(reader.LoadHistoryByBackupId("backup-id"));
        await Task.WhenAll(Enumerable.Range(1, 15).Select(i => Task.Run(() => new LogStore().AppendRun(Run(i)))));
        Assert.Equal(16, reader.LoadHistory("shared").Count);
        Assert.Equal(16, reader.LoadHistoryByBackupId("backup-id").Count);
        Assert.Equal(16, reader.LoadHistory("shared").Select(r => r.StartedUtc).Distinct().Count());
    }

    [Fact]
    public void Retention_prunes_only_expired_owned_logs_and_preserves_inflight_cell_and_external_files()
    {
        using var workspace = new TempWorkspace();
        var store = new LogStore { RetainLastNRuns = 1 };
        var directory = AppPaths.LogDirForBackup("shared");
        Directory.CreateDirectory(directory);
        var expired = Path.Combine(directory, "old.log");
        var recent = Path.Combine(directory, "recent.log");
        var cellLog = Path.Combine(directory, "cell.log");
        var inFlight = Path.Combine(directory, "still-writing.log");
        var unrelated = Path.Combine(workspace.Root, "unrelated.log");
        foreach (var path in new[] { expired, recent, cellLog, inFlight, unrelated }) File.WriteAllText(path, "keep or expire deliberately");
        var oldRun = Run(0); oldRun.LogPath = expired;
        oldRun.Cells.Add(new CellOutcome { LogPath = unrelated });
        store.AppendRun(oldRun);
        Assert.True(File.Exists(inFlight));
        var newRun = Run(1); newRun.LogPath = recent;
        newRun.Cells.Add(new CellOutcome { LogPath = cellLog });
        store.AppendRun(newRun);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(cellLog));
        Assert.True(File.Exists(inFlight));
        Assert.True(File.Exists(unrelated));
        Assert.Single(store.LoadHistory("shared"));
    }

    private static RunRecord Run(int sequence) => new()
    {
        BackupId = "backup-id", BackupName = "shared",
        StartedUtc = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc).AddMinutes(sequence),
        EndedUtc = new DateTime(2026, 10, 2, 0, 0, 1, DateTimeKind.Utc).AddMinutes(sequence),
        Status = BackupRunStatus.Success, Summary = $"run-{sequence}",
    };
}
