using System;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>U173 (9 November 2026): what a run's details say about it.</summary>
public sealed class RunDetailsTests
{
    private static string Id(char c) => new(c, 64);

    [Fact]
    public void The_previous_backup_is_the_newest_snapshot_in_the_repository_list()
    {
        // The service's engine had no history of the app's backups, so the
        // log said "No earlier backup" although restic used a --parent.
        var at = new DateTimeOffset(2026, 11, 9, 10, 34, 34, TimeSpan.Zero);
        var latest = ResticBackrestEngine.LatestOf(new[]
        {
            (Id('a'), at.AddDays(-1).ToUnixTimeMilliseconds()),
            (Id('b'), at.ToUnixTimeMilliseconds()),
            ("not-a-snapshot-id", at.AddDays(1).ToUnixTimeMilliseconds()),
        });
        Assert.Equal((Id('b'), at.UtcDateTime), latest);
        Assert.Null(ResticBackrestEngine.LatestOf(Array.Empty<(string, long)>()));
        // Equal times: restic's order decides, the last one (as Backrest's stable sort).
        Assert.Equal(Id('d'), ResticBackrestEngine.LatestOf(new[] { (Id('c'), 5L), (Id('d'), 5L) })!.Value.Id);

        var dest = new Destination { Name = "Local" };
        var lines = string.Join("\n", ResticBackrestEngine.SetupLines(new Backup(), @"C:\src", dest, latest));
        Assert.Contains("Previous backup to «Local»", lines);
        Assert.Contains("snapshot bbbbbbbb", lines);
        Assert.DoesNotContain("No earlier backup", lines);
        Assert.Contains("No earlier backup", string.Join("\n", ResticBackrestEngine.SetupLines(new Backup(), @"C:\src", dest, null)));
    }

    [Fact]
    public void Only_the_services_schedule_starts_a_run_automatically()
    {
        Assert.True(SystemBackupService.IsAutomatic(new ServiceHostRequest(1, "scheduled", null, "b", "backup")));
        Assert.False(SystemBackupService.IsAutomatic(new ServiceHostRequest(1, "start-cli", Guid.NewGuid().ToString("N"), "b", "backup")));
        Assert.False(SystemBackupService.IsAutomatic(new ServiceHostRequest(1, "start", Guid.NewGuid().ToString("N"), "b", "backup")));
        Assert.True(ServiceApprovedWorker.StartedBySchedule(ownerCommand: false, "1"));
        Assert.False(ServiceApprovedWorker.StartedBySchedule(ownerCommand: true, "1"));
        Assert.False(ServiceApprovedWorker.StartedBySchedule(ownerCommand: false, "0"));
        Assert.False(ServiceApprovedWorker.StartedBySchedule(ownerCommand: false, null));
    }

    [Fact]
    public void The_duration_leaves_out_the_wait_and_the_hover_shows_it()
    {
        // The owner's local_d run of 9 November: 06:00 to 07:01:48, of which
        // 51m 46s waiting for another backup to the same destination.
        var start = new DateTime(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);
        var r = new RunRecord
        {
            StartedUtc = start, EndedUtc = start.AddMinutes(61).AddSeconds(48), Status = BackupRunStatus.Success,
            BytesUploaded = 465_621_263, FilesNew = 115, FilesModified = 6, QueuedFor = TimeSpan.FromSeconds(51 * 60 + 46),
        };
        var summary = BackupOrchestrator.BuildSummary(r);
        Assert.Contains("in 10m 02s", summary);
        Assert.Contains("waited 51m 46s", summary);
        Assert.DoesNotContain("1h 01m", summary);

        var (label, hover) = LastRunLine.For(summary, BackupRunStatus.Success, r.EndedUtc!.Value, r.EndedUtc.Value.AddMinutes(5));
        Assert.DoesNotContain("waited", label);
        Assert.Contains("took 10m 02s", hover);
        Assert.Contains("waited 51m 46s for its turn", hover);

        // Not queued: no mention, and the duration is the whole run.
        r.QueuedFor = TimeSpan.Zero;
        var plain = BackupOrchestrator.BuildSummary(r);
        Assert.Contains("in 1h 01m", plain);
        Assert.DoesNotContain("waited", plain);
        Assert.DoesNotContain("waited", LastRunLine.For(plain, BackupRunStatus.Success, r.EndedUtc.Value, r.EndedUtc.Value).Hover);
    }

    [Fact]
    public void A_wait_longer_than_the_run_never_gives_a_negative_duration()
    {
        var start = new DateTime(2026, 11, 9, 6, 0, 0, DateTimeKind.Utc);
        var r = new RunRecord { StartedUtc = start, EndedUtc = start.AddMinutes(1), QueuedFor = TimeSpan.FromMinutes(2) };
        Assert.Equal(TimeSpan.Zero, r.WorkingTime(DateTime.UtcNow));
    }
}
