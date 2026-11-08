using System;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U157: a backup run by the background service leaves the settings'
/// LastSuccessUtc empty; its success is in the run history. Restore files,
/// Mount and Test restore must still be offered.</summary>
public sealed class RestorableRunsTests
{
    private static RunRecord Run(string backupId, BackupRunStatus status, RunKind kind = RunKind.Backup, BackupRunStatus? cell = null)
    {
        var run = new RunRecord { BackupId = backupId, Kind = kind, Status = status,
            StartedUtc = DateTime.UtcNow.AddMinutes(-20), EndedUtc = DateTime.UtcNow };
        if (cell is { } c) run.Cells.Add(new CellOutcome { SourcePath = @"C:\", DestinationName = "Google Drive", Status = c });
        return run;
    }

    [Fact]
    public void A_background_success_counts_when_the_settings_show_none()
    {
        var backup = new Backup { Name = "local_c" };
        Assert.Null(backup.LastSuccessUtc);
        Assert.True(RestorableRuns.Any(backup, new[] { Run(backup.Id, BackupRunStatus.Success) }));
    }

    [Fact]
    public void An_earlier_success_counts_after_a_later_failure()
        => Assert.True(RestorableRuns.Any(new Backup { Id = "b" },
            new[] { Run("b", BackupRunStatus.Failed), Run("b", BackupRunStatus.Warning) }));

    [Fact]
    public void A_failed_run_whose_one_step_succeeded_stored_something()
        => Assert.True(RestorableRuns.Any(new Backup { Id = "b" }, new[] { Run("b", BackupRunStatus.Failed, cell: BackupRunStatus.Success) }));

    [Fact]
    public void Nothing_stored_by_failures_skips_checks_or_other_backups()
    {
        var backup = new Backup { Id = "b" };
        Assert.False(RestorableRuns.Any(backup, Array.Empty<RunRecord>()));
        Assert.False(RestorableRuns.Any(backup, new[]
        {
            Run("b", BackupRunStatus.Failed, cell: BackupRunStatus.Failed),
            Run("b", BackupRunStatus.Skipped),
            Run("b", BackupRunStatus.Success, RunKind.TestRestore),
            Run("other", BackupRunStatus.Success),
            new RunRecord { BackupId = "b", Status = BackupRunStatus.Running, StartedUtc = DateTime.UtcNow },
        }));
    }
}
