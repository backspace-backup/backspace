using System;
using Backspace.Models;
using Xunit;

namespace Backspace.Tests.Models;

/// <summary>
/// Configuration must remain accessible before an absence and after
/// expiry. S28 also exercises the real Easy controls, Save and reopen.
/// </summary>
public class BackupShouldShowGraceBlockTests
{
    private static readonly DateTime _t0 = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NeverRun_backup_shows_block_for_proactive_configuration()
    {
        var b = new Backup
        {
            LastRunStatus = BackupRunStatus.NeverRun,
            SkipNotificationGraceDays = 7,
            CreatedUtc = _t0.AddDays(-1),
        };
        Assert.True(b.ShouldShowGraceBlock(_t0));
    }

    [Fact]
    public void Successful_recent_run_shows_block_before_an_absence()
    {
        var b = new Backup
        {
            LastRunStatus = BackupRunStatus.Success,
            LastSuccessUtc = _t0.AddHours(-1),
            SkipNotificationGraceDays = 7,
        };
        Assert.True(b.ShouldShowGraceBlock(_t0));
    }

    [Fact]
    public void Skipped_within_grace_shows_block()
    {
        var b = new Backup
        {
            LastRunStatus = BackupRunStatus.Skipped,
            LastSuccessUtc = _t0.AddDays(-2),
            SkipNotificationGraceDays = 7,
        };
        Assert.True(b.ShouldShowGraceBlock(_t0));
    }

    [Fact]
    public void Skipped_past_grace_shows_block_to_allow_reset()
    {
        // Expired grace still permits an intentional reset. A skipped
        // run remains Skipped; the warning never promotes it to Failed.
        // SkipGraceReferenceUtc = max(LastSuccessUtc, ResetUtc,
        // CreatedUtc); set CreatedUtc too so the constructor's
        // default-of-now doesn't push the reference forward.
        var b = new Backup
        {
            LastRunStatus = BackupRunStatus.Skipped,
            LastSuccessUtc = _t0.AddDays(-8),
            CreatedUtc = _t0.AddDays(-10),
            SkipNotificationGraceDays = 7,
        };
        Assert.True(b.ShouldShowGraceBlock(_t0));
    }

    [Fact]
    public void GraceDays_zero_keeps_block_available_to_enable_grace()
    {
        var b = new Backup
        {
            LastRunStatus = BackupRunStatus.Skipped,
            LastSuccessUtc = _t0.AddHours(-1),
            SkipNotificationGraceDays = 0,
        };
        Assert.True(b.ShouldShowGraceBlock(_t0));
    }
}
