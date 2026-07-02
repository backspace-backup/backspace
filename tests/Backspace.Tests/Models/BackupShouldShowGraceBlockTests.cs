using System;
using Backspace.Models;
using Xunit;

namespace Backspace.Tests.Models;

/// <summary>
/// Easy mode hides the "skipped-run alerts" block by default; it
/// surfaces only when both halves of the condition hold — most recent
/// run was Skipped AND we're still inside the grace window. Advanced
/// mode shows it unconditionally so power users can configure / reset
/// regardless of run state.
/// </summary>
public class BackupShouldShowGraceBlockTests
{
    private static readonly DateTime _t0 = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NeverRun_backup_does_not_show_block()
    {
        var b = new Backup
        {
            LastRunStatus = BackupRunStatus.NeverRun,
            SkipNotificationGraceDays = 7,
            CreatedUtc = _t0.AddDays(-1),
        };
        Assert.False(b.ShouldShowGraceBlock(_t0));
    }

    [Fact]
    public void Successful_recent_run_does_not_show_block()
    {
        var b = new Backup
        {
            LastRunStatus = BackupRunStatus.Success,
            LastSuccessUtc = _t0.AddHours(-1),
            SkipNotificationGraceDays = 7,
        };
        Assert.False(b.ShouldShowGraceBlock(_t0));
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
    public void Skipped_past_grace_does_not_show_block()
    {
        // Grace 7d, last success 8d ago → grace expired; the block
        // would suggest "reset" but resetting is meaningless when the
        // window has already escalated to Failed. Hide.
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
        Assert.False(b.ShouldShowGraceBlock(_t0));
    }

    [Fact]
    public void GraceDays_zero_disables_block_regardless()
    {
        var b = new Backup
        {
            LastRunStatus = BackupRunStatus.Skipped,
            LastSuccessUtc = _t0.AddHours(-1),
            SkipNotificationGraceDays = 0,
        };
        Assert.False(b.ShouldShowGraceBlock(_t0));
    }
}
