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
/// same folder, which the service runs.</summary>
internal static class SystemServiceRuns
{
    /// <summary>A backup the service can run on its own: complete, with no
    /// custom engine arguments, and only network shares it can sign in to.</summary>
    internal static bool CanRun(Backup backup, AppConfig config)
        => backup.Enabled && backup.SourcePaths is { Count: > 0 } && backup.SourcePaths.All(p => !string.IsNullOrWhiteSpace(p))
            && backup.Targets is { Count: > 0 } && backup.Targets.All(t => config.Destinations.Any(d => d.Id == t.DestinationId))
            && string.IsNullOrWhiteSpace(backup.ExtraBackupArgs) && backup.Schedule is not null
            && SchedulePlan.ServiceCanRun(backup, config.Destinations);

    internal static ServiceRunAuthorization.AuthorizedRun Authorize(string installation, string backupId, string operation, bool ownerCommand)
        => Authorize(ServiceConfigurationComparison.ReadDesktop(installation), backupId, operation, ownerCommand);

    internal static ServiceRunAuthorization.AuthorizedRun Authorize(AppConfig config, string backupId, string operation, bool ownerCommand)
    {
        var backup = config.Backups.SingleOrDefault(b => b.Id == backupId)
            ?? throw new InvalidDataException("This backup no longer exists.");
        if (!CanRun(backup, config))
            throw new NotSupportedException("This backup runs from the Backspace window, not the background service.");
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
