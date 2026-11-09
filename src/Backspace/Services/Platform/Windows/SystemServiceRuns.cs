using System;
using System.IO;
using System.Linq;
using System.Security;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Which of the owner's saved backups the version-3 service runs
/// (U100). It reads Backspace.config\config.json as the owner saved it: a
/// change applies at the next scheduled check, with no separate approval.
/// Whoever can change these settings can already replace Backspace.exe in the
/// same folder, which the service runs.
///
/// <para>Two questions, kept apart (U171): whether the service is able to run
/// a backup (<see cref="CanRun"/>), and whether it may start it on its own
/// (<see cref="RunsOnItsOwn"/>). With the service on, every backup it can run
/// goes through it when the owner asks (Run now, Save and run, the command
/// line), so a backup with automatic backups off gets the same Windows
/// snapshot as a scheduled one. Only backups with automatic backups on are
/// ever started by its schedule.</para></summary>
internal static class SystemServiceRuns
{
    /// <summary>A backup the service is able to run: complete, with no
    /// custom engine arguments, and only network shares it can sign in to.
    /// Says nothing about whether it runs automatically.</summary>
    internal static bool CanRun(Backup backup, AppConfig config)
        => backup.SourcePaths is { Count: > 0 } && backup.SourcePaths.All(p => !string.IsNullOrWhiteSpace(p))
            && backup.Targets is { Count: > 0 } && backup.Targets.All(t => config.Destinations.Any(d => d.Id == t.DestinationId))
            && string.IsNullOrWhiteSpace(backup.ExtraBackupArgs)
            && SchedulePlan.ServiceCanRun(backup, config.Destinations);

    /// <summary>A backup the service's schedule starts without being asked:
    /// one it can run, with automatic backups on.</summary>
    internal static bool RunsOnItsOwn(Backup backup, AppConfig config)
        => backup.Enabled && backup.Schedule is not null && CanRun(backup, config);

    internal static ServiceRunAuthorization.AuthorizedRun Authorize(string installation, string backupId, string operation, bool ownerCommand)
        => Authorize(ServiceConfigurationComparison.ReadDesktop(installation), backupId, operation, ownerCommand);

    /// <param name="ownerCommand">The owner asked for this run through their
    /// authenticated connection (Run now, Save and run, the command line).
    /// Only then may a backup with automatic backups off run; the schedule
    /// and every other caller are refused.</param>
    internal static ServiceRunAuthorization.AuthorizedRun Authorize(AppConfig config, string backupId, string operation, bool ownerCommand)
    {
        var backup = config.Backups.SingleOrDefault(b => b.Id == backupId)
            ?? throw new InvalidDataException("This backup no longer exists.");
        if (!CanRun(backup, config))
            throw new NotSupportedException("This backup runs from the Backspace window, not the background service.");
        if (!ownerCommand && !RunsOnItsOwn(backup, config))
            throw new SecurityException("Automatic backups are off for this backup: it runs only when asked.");
        var kind = operation switch
        {
            "backup" => RunKind.Backup,
            "check" when ownerCommand || backup.CheckSchedule is not null => RunKind.Check,
            "prune" when ownerCommand || backup.PruneSchedule is not null => RunKind.Prune,
            _ => throw new SecurityException("This operation is not run by the background service."),
        };
        return new("current", backup, config, kind);
    }
}
