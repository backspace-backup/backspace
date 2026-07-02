using System;
using Backspace.Models;
using Xunit;

namespace Backspace.Tests.Models;

/// <summary>
/// Pin the grace-period clock math and the 1-per-day notification cap.
/// These are pure functions of model state — orchestrator integration
/// is exercised separately by the E2E scenarios.
/// </summary>
public class BackupGraceTests
{
    private static readonly DateTime AnchorUtc = new(2026, 06, 10, 12, 00, 00, DateTimeKind.Utc);

    [Fact]
    public void SkipGraceReferenceUtc_NeverRun_FallsBackToCreatedUtc()
    {
        // A brand-new backup has no LastSuccess and no manual reset.
        // The grace window has to start counting from somewhere, and
        // CreatedUtc is the only honest reference — otherwise a
        // never-worked backup would surface as Failed on its very
        // first Skip, giving the user no time to investigate before
        // their first notification.
        var b = new Backup { CreatedUtc = AnchorUtc.AddDays(-2) };
        Assert.Equal(b.CreatedUtc, b.SkipGraceReferenceUtc);
    }

    [Fact]
    public void SkipGraceReferenceUtc_PicksLatestOfSuccess_Reset_Created()
    {
        // Three timestamps in scope; the helper always picks the newest.
        var b = new Backup
        {
            CreatedUtc = AnchorUtc.AddDays(-30),
            LastSuccessUtc = AnchorUtc.AddDays(-10),
            SkipNotificationGraceResetUtc = AnchorUtc.AddDays(-3),
        };
        Assert.Equal(AnchorUtc.AddDays(-3), b.SkipGraceReferenceUtc);
    }

    [Theory]
    [InlineData(7, -5, true)]   // 5 days into a 7-day window → grace active
    [InlineData(7, -7, false)]  // exactly at the boundary → grace EXHAUSTED (strict <)
    [InlineData(7, -8, false)]  // past the window → grace exhausted
    [InlineData(0, -1, false)]  // grace=0 disables the window entirely
    [InlineData(30, -29, true)] // long-window backup still inside on day 29
    public void IsWithinSkipGrace_ReturnsExpected(int graceDays, int lastSuccessDeltaDays, bool expected)
    {
        var b = new Backup
        {
            CreatedUtc = AnchorUtc.AddYears(-1),
            LastSuccessUtc = AnchorUtc.AddDays(lastSuccessDeltaDays),
            SkipNotificationGraceDays = graceDays,
        };
        Assert.Equal(expected, b.IsWithinSkipGrace(AnchorUtc));
    }

    [Fact]
    public void IsWithinSkipGrace_HonoursManualResetEvenWhenSuccessIsOlder()
    {
        // Going-away scenario: backup last succeeded 14 days ago.
        // Grace is 7 days. Without the manual reset, the next Skip
        // would already be Failed. After clicking "Reset grace
        // period" today, the next Skip should stay orange for
        // another 7 days.
        var b = new Backup
        {
            CreatedUtc = AnchorUtc.AddYears(-1),
            LastSuccessUtc = AnchorUtc.AddDays(-14),
            SkipNotificationGraceResetUtc = AnchorUtc.AddHours(-1),
            SkipNotificationGraceDays = 7,
        };
        Assert.True(b.IsWithinSkipGrace(AnchorUtc));
    }

    [Theory]
    // SAME status repeats are capped within 24h.
    [InlineData(0, BackupRunStatus.Failed, BackupRunStatus.Failed, false)] // never notified → cap doesn't apply
    [InlineData(1, BackupRunStatus.Failed, BackupRunStatus.Failed, true)]  // notified 1h ago, same status → cap
    [InlineData(23, BackupRunStatus.Failed, BackupRunStatus.Failed, true)] // 23h ago, same status → still cap
    [InlineData(24, BackupRunStatus.Failed, BackupRunStatus.Failed, false)]// exactly 24h → cap released
    [InlineData(48, BackupRunStatus.Failed, BackupRunStatus.Failed, false)]// long past → cap released
    // STATUS TRANSITIONS always break through the cap, even within
    // the 24h window — this was the silent-failure footgun code-review
    // caught: Success at 09:00 must not silence Failed at 17:00.
    [InlineData(1, BackupRunStatus.Success, BackupRunStatus.Failed, false)]
    [InlineData(23, BackupRunStatus.Success, BackupRunStatus.Failed, false)]
    [InlineData(1, BackupRunStatus.Failed, BackupRunStatus.Success, false)]  // recovery breaks through too
    [InlineData(1, BackupRunStatus.Skipped, BackupRunStatus.Failed, false)]  // escalation breaks through
    [InlineData(1, BackupRunStatus.Failed, BackupRunStatus.Skipped, false)]
    public void IsWithinNotificationDailyCap_ReturnsExpected(
        int hoursSinceLastFire,
        BackupRunStatus lastStatus,
        BackupRunStatus newStatus,
        bool expected)
    {
        var b = new Backup
        {
            CreatedUtc = AnchorUtc.AddYears(-1),
            LastNotificationFiredUtc = hoursSinceLastFire == 0
                ? null
                : AnchorUtc.AddHours(-hoursSinceLastFire),
            LastNotifiedStatus = hoursSinceLastFire == 0 ? null : lastStatus,
        };
        Assert.Equal(expected, b.IsWithinNotificationDailyCap(AnchorUtc, newStatus));
    }

    [Fact]
    public void IsWithinNotificationDailyCap_PreUpgradeRow_BreaksThroughOnAnyStatus()
    {
        // Upgrade scenario: a pre-LastNotifiedStatus build leaves
        // LastNotificationFiredUtc populated and LastNotifiedStatus
        // null. Round-4 (THIS commit) rolls back round-3's "treat null
        // as cap-active by 24h" — that variant reintroduced the
        // exact silent-failed-after-success footgun the per-status
        // cap was added to fix. The new behaviour: when the prior
        // notified status is unknown (null), assume the worst and
        // break through. One duplicate toast on the first post-
        // upgrade run is strictly better than swallowing a real
        // Failed transition for up to 24 hours.
        var b = new Backup
        {
            CreatedUtc = AnchorUtc.AddYears(-1),
            LastNotificationFiredUtc = AnchorUtc.AddHours(-2), // 2h ago
            LastNotifiedStatus = null,                        // upgrade scenario
        };
        Assert.False(b.IsWithinNotificationDailyCap(AnchorUtc, BackupRunStatus.Failed));
        Assert.False(b.IsWithinNotificationDailyCap(AnchorUtc, BackupRunStatus.Success));
        Assert.False(b.IsWithinNotificationDailyCap(AnchorUtc, BackupRunStatus.Skipped));
    }
}
