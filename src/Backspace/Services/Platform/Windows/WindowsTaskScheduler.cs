using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.Win32.TaskScheduler;
using Backspace.Models;
using Serilog;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Windows Task Scheduler implementation of <see cref="IScheduler"/>.
/// Matches the original scheduled-task.xml shape:
///   - S4U (runs whether user is logged on or not, no password stored)
///   - Highest run level (needed for VSS on C:\)
///   - StopIfGoingOnBatteries, DisallowStartIfOnBatteries
///   - RunOnlyIfNetworkAvailable
///   - StartWhenAvailable (catch up missed runs)
///   - ExecutionTimeLimit 72h
///
/// Tasks live under \MyTasks\Backspace\ so users can find them
/// easily in Task Scheduler MMC.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTaskScheduler : IScheduler
{
    public const string FolderPath = @"\MyTasks\Backspace";
    private static ILogger _log => AppLogger.For<WindowsTaskScheduler>();

    /// <summary>Current task-name prefix. Tasks registered under any
    /// of the entries in <see cref="LegacyTaskPrefixes"/> are swept
    /// on every <see cref="UpsertTask"/> so a user upgrading from the
    /// predecessor app ("EDB —" prefix) doesn't end up with duplicate
    /// scheduled entries.</summary>
    private const string TaskPrefix = "Backspace";

    /// <summary>Prefixes used by earlier dev builds + the predecessor
    /// app (Duplimate, abbreviated as "EDB" in scheduler entries).
    /// Cleaned on every UpsertTask so re-running registration
    /// converges to the current name even if the user had a stale
    /// task lying around from a previous install. The sweep matches
    /// the patterns "<prefix> — *" exactly so we don't accidentally
    /// nuke unrelated user tasks.</summary>
    private static readonly string[] LegacyTaskPrefixes = { "EDB", "Duplimate" };

    public string TaskNameFor(Backup backup) => $"{TaskPrefix} — {backup.Name}";

    public string TaskNameFor(Backup backup, ScheduledTaskKind kind) => kind switch
    {
        ScheduledTaskKind.Backup => TaskNameFor(backup),
        ScheduledTaskKind.Prune  => $"{TaskPrefix} — {backup.Name} (Prune)",
        ScheduledTaskKind.Check  => $"{TaskPrefix} — {backup.Name} (Check)",
        _ => TaskNameFor(backup),
    };

    public void UpsertTask(Backup backup, string exePath)
    {
        using var ts = new TaskService();
        var folder = ts.RootFolder.CreateFolder(FolderPath, exceptionOnExists: false);

        // Sweep legacy entries before writing the new ones. This
        // converts both EDB → Backspace renames and gets rid of
        // Duplimate-era tasks left over from the predecessor app.
        DeleteLegacyTasksFor(folder, backup);

        // Backup entry — always registered.
        UpsertOne(ts, folder, backup, exePath, ScheduledTaskKind.Backup, backup.Schedule);

        // Prune entry — registered iff PruneSchedule is non-null;
        // otherwise we tear down any leftover Prune entry (the user
        // may have toggled the cadence off in Settings).
        ReconcileOptionalEntry(ts, folder, backup, exePath, ScheduledTaskKind.Prune, backup.PruneSchedule);
        ReconcileOptionalEntry(ts, folder, backup, exePath, ScheduledTaskKind.Check, backup.CheckSchedule);
    }

    /// <summary>Add (or update) the per-kind entry when its schedule
    /// is set; remove it when the schedule is null. The "remove on
    /// null" half is what makes the editor's "turn off prune
    /// cadence" toggle actually clean up the OS scheduler instead
    /// of leaving a dead task.</summary>
    private void ReconcileOptionalEntry(TaskService ts, TaskFolder folder,
        Backup backup, string exePath, ScheduledTaskKind kind, BackupSchedule? schedule)
    {
        var name = TaskNameFor(backup, kind);
        if (schedule is null)
        {
            // Best-effort delete; ignore not-found.
            try { folder.DeleteTask(name, exceptionOnNotExists: false); }
            catch (Exception ex) { _log.Debug(ex, "Reconcile-null delete on {Name} threw (likely doesn't exist)", name); }
            return;
        }
        UpsertOne(ts, folder, backup, exePath, kind, schedule);
    }

    /// <summary>Register / update one scheduled task entry. Same
    /// settings + principal across all three task kinds (backup /
    /// prune / check); the only kind-specific bits are the action
    /// CLI flag and the schedule the trigger is derived from.</summary>
    private void UpsertOne(TaskService ts, TaskFolder folder,
        Backup backup, string exePath, ScheduledTaskKind kind, BackupSchedule schedule)
    {
        _log.Information(
            "Registering scheduled task: name={TaskName} kind={Kind} freq={Frequency} timeOfDay={TimeOfDay} hourly={Hourly} skipOnBattery={SkipOnBattery} stopOnBattery={StopOnBattery} requireNetwork={RequireNetwork} catchUp={CatchUp}",
            TaskNameFor(backup, kind), kind, schedule.Frequency, schedule.TimeOfDay, schedule.HourlyInterval,
            schedule.SkipOnBattery, schedule.StopOnBattery, schedule.RequireNetwork, schedule.CatchUpMissedRuns);

        var td = ts.NewTask();
        td.RegistrationInfo.Description = $"Backspace — {backup.Name}" +
            (kind == ScheduledTaskKind.Backup ? "" : $" ({kind})");
        td.RegistrationInfo.Author = "Backspace";

        // Principal
        var userId = $@"{Environment.UserDomainName}\{Environment.UserName}";
        td.Principal.UserId = userId;
        td.Principal.LogonType = TaskLogonType.S4U;
        td.Principal.RunLevel = TaskRunLevel.Highest;

        // Settings
        td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        td.Settings.StartWhenAvailable = schedule.CatchUpMissedRuns;
        td.Settings.RunOnlyIfNetworkAvailable = schedule.RequireNetwork;
        td.Settings.DisallowStartIfOnBatteries = schedule.SkipOnBattery;
        td.Settings.StopIfGoingOnBatteries = schedule.StopOnBattery;
        td.Settings.Hidden = true;
        td.Settings.Priority = System.Diagnostics.ProcessPriorityClass.BelowNormal;
        td.Settings.ExecutionTimeLimit = schedule.ExecutionTimeLimit;
        td.Settings.WakeToRun = false;
        td.Settings.RunOnlyIfIdle = false;
        td.Settings.AllowHardTerminate = true;
        td.Settings.UseUnifiedSchedulingEngine = true;
        td.Settings.Enabled = backup.Enabled;

        // Trigger
        td.Triggers.Clear();
        var trigger = BuildTrigger(schedule);
        if (trigger is not null) td.Triggers.Add(trigger);

        // Action — cmd.exe wrapper that prefers the user's main exe but
        // falls back to the stub at CanonicalInstaller.StubExePath when
        // the main exe is missing (typical case: user moved the .exe
        // and forgot to reopen the app). The CLI flag varies by kind:
        // --run for backup, --prune for prune, --check for check.
        td.Actions.Clear();
        td.Actions.Add(BuildExecAction(exePath, CanonicalInstaller.StubExePath, backup.Name, kind));

        folder.RegisterTaskDefinition(
            path: TaskNameFor(backup, kind),
            definition: td,
            createType: TaskCreation.CreateOrUpdate,
            userId: userId,
            logonType: TaskLogonType.S4U);
    }

    public void DeleteTask(Backup backup)
    {
        using var ts = new TaskService();
        var folder = ts.GetFolder(FolderPath);
        if (folder is null)
        {
            _log.Debug("DeleteTask: folder {Folder} doesn't exist; nothing to remove for {TaskName}", FolderPath, TaskNameFor(backup));
            return;
        }

        // Remove every entry that could exist for this backup —
        // backup + prune + check. Each name has a known shape so we
        // delete by exact name rather than a regex sweep (which used
        // to risk over-matching if two backups had the same prefix).
        var deleted = 0;
        foreach (var kind in new[] { ScheduledTaskKind.Backup, ScheduledTaskKind.Prune, ScheduledTaskKind.Check })
        {
            var name = TaskNameFor(backup, kind);
            // DeleteTask returns void; probe existence first so we can
            // count actual deletions for the log line.
            var existed = folder.AllTasks.Any(t => t.Name == name);
            try
            {
                folder.DeleteTask(name, exceptionOnNotExists: false);
                if (existed) deleted++;
            }
            catch (Exception ex) { _log.Debug(ex, "DeleteTask({Kind}) threw (likely already gone)", kind); }
        }
        _log.Information("Removed {Count} scheduled task entry(ies) for {TaskName}", deleted, TaskNameFor(backup));
    }

    /// <summary>
    /// Delete any scheduled-task entries under our folder whose name
    /// matches a legacy prefix ("EDB", "Duplimate") for the given
    /// backup. Run on every <see cref="UpsertTask"/> so a user
    /// upgrading from an earlier build / the predecessor app
    /// converges on the current naming without manual cleanup.
    /// Match is "&lt;legacy-prefix&gt; — &lt;backupName&gt;" plus the
    /// "(Prune)" / "(Check)" variants, so we don't touch unrelated
    /// tasks that happen to share a substring.
    /// </summary>
    private void DeleteLegacyTasksFor(TaskFolder folder, Backup backup)
    {
        foreach (var legacy in LegacyTaskPrefixes)
        {
            foreach (var suffix in new[] { "", " (Prune)", " (Check)" })
            {
                var name = $"{legacy} — {backup.Name}{suffix}";
                try
                {
                    if (folder.AllTasks.Any(t => t.Name == name))
                    {
                        folder.DeleteTask(name, exceptionOnNotExists: false);
                        _log.Information("Removed legacy scheduled task {Name}", name);
                    }
                }
                catch (Exception ex) { _log.Debug(ex, "Legacy task sweep on {Name} threw", name); }
            }
        }
    }

    private static Trigger? BuildTrigger(BackupSchedule s)
    {
        // Anchor the StartBoundary on the NEXT occurrence of TimeOfDay,
        // not always Today. If the user registers a daily 02:00 trigger
        // at 09:00, Today.Add(02:00) is in the past — Task Scheduler
        // marks the trigger as already-fired-today and the user sees
        // "no run scheduled for today" until midnight rolls over.
        // StartWhenAvailable on the task settings does catch most of
        // these up, but a fresh next-occurrence anchor is still cleaner
        // (and doesn't depend on the catch-up logic firing in time).
        var next = NextOccurrence(s.TimeOfDay);
        return s.Frequency switch
        {
            ScheduleFrequency.Daily => new DailyTrigger
            {
                StartBoundary = next,
                DaysInterval = 1,
            },
            ScheduleFrequency.Hourly => new TimeTrigger
            {
                StartBoundary = next,
                Repetition = new RepetitionPattern(TimeSpan.FromHours(Math.Max(1, s.HourlyInterval)), TimeSpan.Zero),
            },
            ScheduleFrequency.Weekly => new WeeklyTrigger
            {
                StartBoundary = next,
                WeeksInterval = 1,
                DaysOfWeek = BitmaskToDays(s.WeeklyDaysBitmask),
            },
            // Monthly: fires on the 1st of every month at TimeOfDay.
            // We use the start-boundary's day-of-month so anchoring
            // on a different day (a future feature) is a one-line
            // change. MonthsOfTheYear.AllMonths runs every month;
            // DaysOfMonth=1 pins to the first.
            ScheduleFrequency.Monthly => new MonthlyTrigger
            {
                StartBoundary = new DateTime(next.Year, next.Month, 1, next.Hour, next.Minute, 0, next.Kind),
                DaysOfMonth = new[] { 1 },
                MonthsOfYear = MonthsOfTheYear.AllMonths,
            },
            _ => null,
        };
    }

    /// <summary>
    /// First wall-clock instant at or after <see cref="DateTime.Now"/>
    /// that lands on the configured time-of-day. Today if the slot
    /// hasn't passed yet, tomorrow if it has.
    /// </summary>
    private static DateTime NextOccurrence(TimeSpan timeOfDay)
    {
        var todayAt = DateTime.Today.Add(timeOfDay);
        return todayAt > DateTime.Now ? todayAt : todayAt.AddDays(1);
    }

    private static DaysOfTheWeek BitmaskToDays(int mask)
    {
        DaysOfTheWeek d = 0;
        if ((mask & 1)  != 0) d |= DaysOfTheWeek.Sunday;
        if ((mask & 2)  != 0) d |= DaysOfTheWeek.Monday;
        if ((mask & 4)  != 0) d |= DaysOfTheWeek.Tuesday;
        if ((mask & 8)  != 0) d |= DaysOfTheWeek.Wednesday;
        if ((mask & 16) != 0) d |= DaysOfTheWeek.Thursday;
        if ((mask & 32) != 0) d |= DaysOfTheWeek.Friday;
        if ((mask & 64) != 0) d |= DaysOfTheWeek.Saturday;
        return d;
    }

    /// <summary>
    /// Builds the cmd.exe-wrapped ExecAction. The wrapper pattern:
    /// <code>
    /// cmd /D /S /C "if exist ""MAIN"" ( ""MAIN"" --run ""NAME"" ) else ( ""STUB"" )"
    /// </code>
    /// <para>
    /// Why cmd: it's the smallest dependable scripting layer present on
    /// every Windows install. /D skips AutoRun, /S preserves the whole
    /// quoted command verbatim (necessary because we have multiple
    /// quoted paths inside), /C runs and exits.
    /// </para>
    /// <para>
    /// Quoting: inside cmd /S /C "...", a literal " is written as "".
    /// So each "..." quoted path here is rendered as ""..."" in the
    /// argument string. Backup names are restricted to ^[A-Za-z0-9_-]+$
    /// at validation time, so they can't contain shell metacharacters.
    /// Path arguments come from Environment.ProcessPath / canonical
    /// stub path; on Windows neither contains a " character.
    /// </para>
    /// <para>
    /// Internal so the unit test can assert the exact command-line shape
    /// without needing a real Task Scheduler environment.
    /// </para>
    /// </summary>
    internal static ExecAction BuildExecAction(string mainExe, string stubExe, string backupName,
        ScheduledTaskKind kind = ScheduledTaskKind.Backup)
    {
        var args = BuildCmdWrapperArguments(mainExe, stubExe, backupName, kind);
        var workDir = Path.GetDirectoryName(mainExe);
        if (string.IsNullOrEmpty(workDir)) workDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ExecAction("cmd.exe", args, workDir);
    }

    internal static string BuildCmdWrapperArguments(string mainExe, string stubExe, string backupName,
        ScheduledTaskKind kind = ScheduledTaskKind.Backup)
    {
        var qm = CmdQuote(mainExe);
        var qs = CmdQuote(stubExe);
        var qn = CmdQuote(backupName);
        // The CLI flag the main exe is invoked with depends on the
        // task kind — --run / --prune / --check each map to a
        // RunMode in Program.cs. The fallback-stub branch stays
        // identical: the stub's job is just to pop "open Backspace
        // from its new location" regardless of which task fired it.
        var flag = kind switch
        {
            ScheduledTaskKind.Prune => "--prune",
            ScheduledTaskKind.Check => "--check",
            _                       => "--run",
        };
        return $@"/D /S /C ""if exist {qm} ( {qm} {flag} {qn} ) else ( {qs} )""";
    }

    /// <summary>
    /// Quote a token for inclusion inside the outer <c>cmd /S /C "..."</c>
    /// string. Each literal <c>"</c> becomes <c>""</c> per cmd's quoting
    /// rule for the quoted command.
    /// </summary>
    private static string CmdQuote(string s) => "\"\"" + s.Replace("\"", "\"\"\"\"") + "\"\"";
}
