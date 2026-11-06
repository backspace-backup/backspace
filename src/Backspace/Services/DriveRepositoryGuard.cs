using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Keeps a backup from starting over on the wrong external drive or network
/// share (U117; shares since 4 November 2026). A drive with the expected name
/// (or letter) but without this backup's repository may be a different drive
/// with the same name, or the same drive erased; a share at the same address
/// may be a replaced or wiped NAS. Backspace then never creates a new
/// repository by itself:
/// <list type="bullet">
///   <item>The very first backup to a drive runs when the person starts it
///   (Run now, Save &amp; Run): starting it is the confirmation.</item>
///   <item>Afterwards a run goes ahead only when the repository is there.
///   Otherwise the app asks, and unattended runs are skipped until it has.</item>
///   <item>"Don't use this drive" leaves a marker file at the drive's or
///   share's root (or, when a share's root is read-only, beside the backup's
///   folder), named after the backup, so it is skipped for that backup only,
///   on any computer, without asking again.</item>
/// </list>
/// The answers live on the drive itself, so the background service and the
/// app read the same decision without a settings change.
/// </summary>
internal static class DriveRepositoryGuard
{
    internal enum State
    {
        /// <summary>Not an external drive or network share, or it isn't attached or reachable (other checks report that).</summary>
        NotApplicable,
        /// <summary>The repository is on the drive.</summary>
        Present,
        /// <summary>The person allowed a new backup on this drive.</summary>
        Accepted,
        /// <summary>The person chose not to use this drive for this backup.</summary>
        Declined,
        /// <summary>No repository and no answer yet.</summary>
        Missing,
    }

    internal const string DeclinedSuffix = "declined";
    internal const string AcceptedSuffix = "accepted";

    /// <summary>The repository folder on the attached drive or the share, or
    /// null when the guard does not apply.</summary>
    internal static string? RepositoryPath(Destination destination)
    {
        if (!OperatingSystem.IsWindows()) return null;
        if (destination.Kind == DestinationKind.NetworkShare)
            return string.IsNullOrWhiteSpace(destination.PathOrSubpath) ? null
                : Path.TrimEndingDirectorySeparator(destination.PathOrSubpath.Trim());
        if (destination.Kind != DestinationKind.ExternalDrive) return null;
        var check = VolumeDetector.Check(destination.ExpectedDriveLetter, destination.ExpectedVolumeLabel);
        if (check.Verdict != VolumeDetector.Verdict.Mounted) return null;
        var path = destination.PathOrSubpath ?? "";
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':') return null;
        if (string.IsNullOrWhiteSpace(destination.ExpectedDriveLetter) && check.MountedRoot is { } root)
            path = ResticUriResolver.RebaseDrivePath(path, root);
        return Path.TrimEndingDirectorySeparator(path);
    }

    /// <summary>Tests keep markers out of the real drive root.</summary>
    internal static string? MarkerRootOverride;

    internal static string MarkerName(string backupId, string suffix) => $".backspace.{backupId}.{suffix}";

    /// <summary>Where answers are kept: the drive's or share's root, then the
    /// folder holding the backup's folder for a share whose root is read-only.</summary>
    internal static IEnumerable<string> MarkerFolders(string repositoryPath)
    {
        if (MarkerRootOverride is { } overridden) { yield return overridden; yield break; }
        var root = Path.GetPathRoot(repositoryPath)!;
        yield return root;
        if (Path.GetDirectoryName(repositoryPath) is { Length: > 0 } parent
            && !string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            yield return parent;
    }

    private static bool HasMarker(string repositoryPath, string backupId, string suffix) =>
        MarkerFolders(repositoryPath).Any(folder => File.Exists(Path.Combine(folder, MarkerName(backupId, suffix))));

    /// <summary>A restic repository keeps its <c>config</c> file at its root.</summary>
    internal static bool HasRepository(string repositoryPath) =>
        File.Exists(Path.Combine(repositoryPath, "config"));

    internal static State Check(Backup backup, Destination destination)
    {
        if (RepositoryPath(destination) is not { } repo) return State.NotApplicable;
        if (HasRepository(repo)) return State.Present;
        // An unreachable share is reported by the reachability check, not as a missing backup.
        if (destination.Kind == DestinationKind.NetworkShare && !Directory.Exists(Path.GetPathRoot(repo))) return State.NotApplicable;
        if (HasMarker(repo, backup.Id, DeclinedSuffix)) return State.Declined;
        if (HasMarker(repo, backup.Id, AcceptedSuffix)) return State.Accepted;
        return State.Missing;
    }

    /// <summary>Allows the next run to start a new backup on this drive.</summary>
    internal static void Accept(Backup backup, Destination destination)
    {
        var repo = RepositoryPath(destination) ?? throw new IOException("The drive is not attached.");
        TryDelete(repo, backup.Id, DeclinedSuffix);
        WriteMarker(repo, backup, AcceptedSuffix,
            "Backspace may start this backup here. This file is removed after the first successful backup.");
    }

    /// <summary>Remembers on the drive that this backup must not use it.</summary>
    internal static void Decline(Backup backup, Destination destination)
    {
        var repo = RepositoryPath(destination) ?? throw new IOException("The drive is not attached.");
        TryDelete(repo, backup.Id, AcceptedSuffix);
        WriteMarker(repo, backup, DeclinedSuffix,
            "Backspace does not back up this backup here. Delete this file, or choose Run now in Backspace, to use this place again.");
    }

    /// <summary>After a successful backup the repository itself is the proof.</summary>
    internal static void ClearAccepted(Backup backup, Destination destination)
    {
        try
        {
            if (RepositoryPath(destination) is { } repo && HasRepository(repo))
                TryDelete(repo, backup.Id, AcceptedSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
    }

    /// <summary>True when an earlier backup of <paramref name="backup"/> to
    /// <paramref name="destination"/> succeeded, so a missing repository is
    /// not a first backup.</summary>
    internal static bool SucceededBefore(Backup backup, Destination destination, LogStore? logs)
    {
        try
        {
            if (logs?.LoadHistoryByBackupId(backup.Id).Any(r => r.Kind == RunKind.Backup && r.Cells.Any(c =>
                    c.DestinationName == destination.Name && c.Status is BackupRunStatus.Success or BackupRunStatus.Warning)) == true)
                return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        // Older records may lack cells; a single-destination backup that
        // succeeded once has used this drive.
        return backup.LastSuccessUtc is not null && backup.Targets.Count == 1 && backup.Targets[0].DestinationId == destination.Id;
    }

    /// <summary>Why an unattended run was skipped.</summary>
    internal static string SkipReason(State state, Backup backup, Destination destination)
    {
        var share = destination.Kind == DestinationKind.NetworkShare;
        var place = share ? "network share" : "drive attached";
        return state == State.Declined
            ? $"Skipped: you chose not to back up «{backup.Name}» to the {place} now used for «{destination.Name}». Choose Run now to use it again."
            : $"Waiting for you: the {place} for «{destination.Name}» has no backup of «{backup.Name}» at {destination.PathOrSubpath}. "
              + (share ? "It may be a different or erased network drive at the same address."
                       : "It may be a different drive with the same name, or the drive was erased.")
              + " Open Backspace and choose Run now to decide.";
    }

    private static void WriteMarker(string repositoryPath, Backup backup, string suffix, string note)
    {
        Exception? failure = null;
        foreach (var folder in MarkerFolders(repositoryPath))
        {
            var path = Path.Combine(folder, MarkerName(backup.Id, suffix));
            try
            {
                File.WriteAllText(path, $"Backup: {backup.Name}\nBackup ID: {backup.Id}\nWritten: {DateTime.Now:yyyy-MM-dd HH:mm}\n\n{note}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; continue; }
            try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return;
        }
        throw failure ?? new IOException("Backspace could not write its note here.");
    }

    private static void TryDelete(string repositoryPath, string backupId, string suffix)
    {
        foreach (var folder in MarkerFolders(repositoryPath))
        {
            var path = Path.Combine(folder, MarkerName(backupId, suffix));
            try
            {
#if WINDOWS
                // Other accounts may be able to change a drive's folders: a
                // folder swapped for a link would turn this delete, run by
                // Windows itself in the service, onto any file (U152). Only
                // the note at this exact path is deleted, through its handle.
                Platform.Windows.ExactPathDelete.File(path);
#else
                if (File.Exists(path)) File.Delete(path);
#endif
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
    }
}
