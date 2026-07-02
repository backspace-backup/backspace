using Backspace.Models;

namespace Backspace.Services.Platform;

/// <summary>What kind of OS-scheduler entry a given task represents.
/// Each Backup may have up to three: the backup itself + an optional
/// standalone prune cadence + an optional standalone check cadence.
/// The implementation maps each to a distinct OS-scheduler item with
/// its own trigger, so a user can have "Backup daily 20:00" + "Prune
/// Sunday 03:00" + "Check 1st-of-month 03:00" side by side.</summary>
public enum ScheduledTaskKind
{
    Backup,
    Prune,
    Check,
}

/// <summary>
/// Platform-neutral scheduling surface. Each desktop OS has its own
/// scheduler (Windows Task Scheduler, macOS launchd, Linux systemd
/// user units / cron); this interface is the only contract the rest
/// of the app needs to know about.
///
/// <para>
/// Implementations are expected to be idempotent: <see cref="UpsertTask"/>
/// reconciles whatever's currently registered with the latest definition
/// from <paramref name="backup"/>; <see cref="DeleteTask"/> is a no-op
/// when nothing is registered. Each call reconciles ALL the tasks for
/// the backup — backup, prune, check — adding new entries when their
/// schedules go from null to set, and removing the entries when they
/// go back to null.
/// </para>
/// </summary>
public interface IScheduler
{
    /// <summary>
    /// Register or update the OS-scheduler entries for the backup.
    /// Always registers the main Backup entry; additionally registers
    /// Prune and Check entries when <see cref="Backup.PruneSchedule"/>
    /// / <see cref="Backup.CheckSchedule"/> are non-null. Removes
    /// previously-registered entries when their corresponding
    /// schedule is null. Caller passes <paramref name="exePath"/> so
    /// the scheduler doesn't have to second-guess where the user
    /// keeps the app.
    /// </summary>
    void UpsertTask(Backup backup, string exePath);

    /// <summary>Remove every scheduler entry for this backup (backup
    /// + prune + check, if registered). Safe to call when nothing is
    /// registered.</summary>
    void DeleteTask(Backup backup);

    /// <summary>Stable label used by the underlying scheduler for
    /// the backup entry. <see cref="TaskNameFor(Backup, ScheduledTaskKind)"/>
    /// overload returns the per-kind label.</summary>
    string TaskNameFor(Backup backup);

    /// <summary>Per-kind label. Default impl is "{TaskNameFor(backup)}
    /// — {kind}" but each platform can override.</summary>
    string TaskNameFor(Backup backup, ScheduledTaskKind kind) =>
        kind == ScheduledTaskKind.Backup
            ? TaskNameFor(backup)
            : $"{TaskNameFor(backup)} ({kind})";
}
