using System;
using Backspace.Models;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// D-4 / D-5: model-level pins for the two notification-noise dampers
/// the orchestrator wires up — the per-backup skip-grace window and the
/// 24h-per-status notification cap. The orchestrator's call sites are
/// covered by BuildSummary tests + Reporting integration; these pin the
/// pure functions on Backup so a refactor of the predicate logic
/// doesn't silently regress either damper.
///
/// History note: round-3 of the review cycle attempted to treat
/// LastNotifiedStatus=null as "cap active for 24h" and REINTRODUCED the
/// original silent-Failed-after-Success bug for post-upgrade users.
/// Round-4 reverted to "null breaks through" — these tests pin that
/// reversal so a future tidy-up of the upgrade path can't re-regress.
/// </summary>
public class BackupGraceAndCapTests
{
    private static Backup MakeBackup(DateTime createdUtc, DateTime? lastSuccessUtc = null, int graceDays = 7)
    {
        return new Backup
        {
            CreatedUtc = createdUtc,
            LastSuccessUtc = lastSuccessUtc,
            SkipNotificationGraceDays = graceDays,
        };
    }

    // ---- Skip-notification grace ---------------------------------

    [Fact]
    public void Grace_FirstSkippedRunWithinGraceDays_StaysOrange()
    {
        // A fresh backup that's never succeeded and just hit its first
        // Skipped (offline NAS, weekend at the cabin) should NOT
        // escalate the notification to Failed during the grace window
        // — that was the user-reported "I get a panic email every day
        // I'm offline" bug that the grace window solves.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = MakeBackup(createdUtc: now.AddDays(-3));
        Assert.True(b.IsWithinSkipGrace(now));
    }

    [Fact]
    public void Grace_SkippedRunPastGraceDays_AllowsWarningNotification()
    {
        // 8 days past the reference (no LastSuccess, no Reset) with a
        // 7-day grace means the next Skipped run is eligible for a
        // warning notification, while its stored status stays Skipped.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = MakeBackup(createdUtc: now.AddDays(-8));
        Assert.False(b.IsWithinSkipGrace(now));
    }

    [Fact]
    public void Grace_BoundaryAtExactlyGraceDays_AllowsWarningNotification()
    {
        // The predicate uses strict <, so elapsed == grace is OUT of
        // the window. Notifications become eligible at this boundary.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = MakeBackup(createdUtc: now.AddDays(-7));
        Assert.False(b.IsWithinSkipGrace(now));
    }

    [Fact]
    public void Grace_LastSuccessAdvancesReference_RestartsGrace()
    {
        // A successful run pushes SkipGraceReferenceUtc forward,
        // restarting the grace window from that point. Confirms the
        // max(CreatedUtc, LastSuccessUtc, ResetUtc) shape — a
        // refactor that picked CreatedUtc unconditionally would
        // silently break this.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = MakeBackup(
            createdUtc: now.AddDays(-365), // way past grace by creation alone
            lastSuccessUtc: now.AddDays(-2));
        Assert.True(b.IsWithinSkipGrace(now));
    }

    [Fact]
    public void Grace_ResetTimestampAdvancesReference_RestartsGrace()
    {
        // User-pressed "Reset grace period" in Onboarding/Settings.
        // Behaves like a LastSuccess for the purposes of the window —
        // gives the user a fresh 7 days to leave town and not get
        // emailed about it.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = MakeBackup(
            createdUtc: now.AddDays(-365),
            lastSuccessUtc: now.AddDays(-365)); // both ancient
        b.SkipNotificationGraceResetUtc = now.AddDays(-1);
        Assert.True(b.IsWithinSkipGrace(now));
    }

    [Fact]
    public void Grace_ZeroDays_DisablesGraceEntirely()
    {
        // A user who explicitly sets grace=0 wants every Skipped run
        // to escalate immediately. Predicate must short-circuit
        // before the elapsed-time check — otherwise a fresh backup
        // would briefly be "within grace" for 0 days, which the call
        // site reads as still-orange.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = MakeBackup(createdUtc: now, graceDays: 0);
        Assert.False(b.IsWithinSkipGrace(now));
    }

    // ---- D-5: notification daily cap (1/day per backup per status) -

    [Fact]
    public void Cap_FirstEverNotification_BreaksThrough()
    {
        // LastNotificationFiredUtc null means "we've never sent a
        // notification for this backup" — never cap a first-ever fire.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = new Backup { LastNotificationFiredUtc = null, LastNotifiedStatus = null };
        Assert.False(b.IsWithinNotificationDailyCap(now, BackupRunStatus.Failed));
    }

    [Fact]
    public void Cap_SameStatusWithin24h_Suppresses()
    {
        // Daily-cap shape: a second Failed within 24h of the previous
        // Failed must be suppressed so a backup hitting the same
        // failure mode hourly doesn't fire 24 toasts a day.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = new Backup
        {
            LastNotificationFiredUtc = now.AddHours(-3),
            LastNotifiedStatus = BackupRunStatus.Failed,
        };
        Assert.True(b.IsWithinNotificationDailyCap(now, BackupRunStatus.Failed));
    }

    [Fact]
    public void Cap_SameStatusPast24h_BreaksThrough()
    {
        // The 24h is rolling: a Failed at 09:00 yesterday must not
        // suppress a Failed at 10:00 today.
        var now = new DateTime(2026, 6, 10, 10, 0, 0, DateTimeKind.Utc);
        var b = new Backup
        {
            LastNotificationFiredUtc = now.AddHours(-25),
            LastNotifiedStatus = BackupRunStatus.Failed,
        };
        Assert.False(b.IsWithinNotificationDailyCap(now, BackupRunStatus.Failed));
    }

    [Fact]
    public void Cap_StatusTransition_BreaksThroughEvenIfWithin24h()
    {
        // The original "single timestamp, any severity" cap silently
        // swallowed a Failed notification at 17:00 after a Success at
        // 09:00 same day. The per-status cap MUST let the transition
        // through — this is the bug the round-1 review caught. Pin it
        // here so a "simplification" PR can't merge the field back.
        var now = new DateTime(2026, 6, 10, 17, 0, 0, DateTimeKind.Utc);
        var b = new Backup
        {
            LastNotificationFiredUtc = now.AddHours(-8),
            LastNotifiedStatus = BackupRunStatus.Success,
        };
        Assert.False(b.IsWithinNotificationDailyCap(now, BackupRunStatus.Failed));
    }

    [Fact]
    public void Cap_PostUpgrade_NullLastStatus_BreaksThrough()
    {
        // ROUND-3 of the review cycle treated null LastNotifiedStatus
        // as "cap active for 24h" — that REINTRODUCED the silent-
        // Failed-after-Success bug for the post-upgrade case (the
        // pre-upgrade Success populated LastNotificationFiredUtc but
        // not LastNotifiedStatus, and the new build saw null and
        // suppressed). Round-4 reversed: null breaks through. The
        // worst case is one duplicate toast on the first post-upgrade
        // run, strictly better than swallowing a real Failed.
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var b = new Backup
        {
            LastNotificationFiredUtc = now.AddHours(-2),
            LastNotifiedStatus = null,
        };
        Assert.False(b.IsWithinNotificationDailyCap(now, BackupRunStatus.Failed));
    }
}
