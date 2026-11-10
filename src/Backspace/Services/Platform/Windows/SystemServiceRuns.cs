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
    /// <summary>A backup the service is able to run: one with folders to back
    /// up and destinations that exist. Says nothing about whether it runs
    /// automatically. With the service set up it runs every backup (U171):
    /// a network share without its saved user name and password fails at the
    /// run with a message saying so (ServiceNetworkShares), never in the window.</summary>
    internal static bool CanRun(Backup backup, AppConfig config) => Refusal(backup, config) is null;

    /// <summary>Why the service can't run this backup, for the person; null
    /// when it can.</summary>
    internal static string? Refusal(Backup backup, AppConfig config)
    {
        if (backup.SourcePaths is not { Count: > 0 } || backup.SourcePaths.Any(string.IsNullOrWhiteSpace))
            return "This backup has no folders to back up. Edit it and choose them.";
        if (backup.Targets is not { Count: > 0 } || !backup.Targets.All(t => config.Destinations.Any(d => d.Id == t.DestinationId)))
            return "This backup has no destination to save to. Edit it and choose one.";
        return null;
    }

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
        if (Refusal(backup, config) is { } refusal)
            throw new NotSupportedException(refusal);
        if (!ownerCommand && !RunsOnItsOwn(backup, config))
            throw new SecurityException("Automatic backups are off for this backup: it runs only when asked.");
        var kind = operation switch
        {
            "backup" => RunKind.Backup,
            "check" when ownerCommand || backup.Checks.Any(c => c.Schedule is not null) => RunKind.Check,
            "prune" when ownerCommand || backup.PruneSchedule is not null => RunKind.Prune,
            _ => throw new SecurityException("This operation is not run by the background service."),
        };
        return new("current", backup, config, kind);
    }
}
