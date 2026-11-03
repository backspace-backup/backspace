using System;
using System.IO;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Keeps a backup from starting over on the wrong external drive (U117).
/// A drive with the expected name (or letter) but without this backup's
/// repository may be a different drive with the same name, or the same
/// drive erased. Backspace then never creates a new repository by itself:
/// <list type="bullet">
///   <item>The very first backup to a drive runs when the person starts it
///   (Run now, Save &amp; Run): starting it is the confirmation.</item>
///   <item>Afterwards a run goes ahead only when the repository is there.
///   Otherwise the app asks, and unattended runs are skipped until it has.</item>
///   <item>"Don't use this drive" leaves a marker file at the drive's root,
///   named after the backup, so that drive is skipped for that backup only,
///   on any computer, without asking again.</item>
/// </list>
/// The answers live on the drive itself, so the background service and the
/// app read the same decision without a settings change.
/// </summary>
internal static class DriveRepositoryGuard
{
    internal enum State
    {
        /// <summary>Not an external drive, or the drive isn't attached (the volume check reports that).</summary>
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

    /// <summary>The repository folder on the attached drive, or null when the
    /// guard does not apply.</summary>
    internal static string? RepositoryPath(Destination destination)
    {
        if (destination.Kind != DestinationKind.ExternalDrive || !OperatingSystem.IsWindows()) return null;
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

    internal static string MarkerPath(string repositoryPath, string backupId, string suffix) =>
        Path.Combine(MarkerRootOverride ?? Path.GetPathRoot(repositoryPath)!, $".backspace.{backupId}.{suffix}");

    /// <summary>A restic repository keeps its <c>config</c> file at its root.</summary>
    internal static bool HasRepository(string repositoryPath) =>
        File.Exists(Path.Combine(repositoryPath, "config"));

    internal static State Check(Backup backup, Destination destination)
    {
        if (RepositoryPath(destination) is not { } repo) return State.NotApplicable;
        if (HasRepository(repo)) return State.Present;
        if (File.Exists(MarkerPath(repo, backup.Id, DeclinedSuffix))) return State.Declined;
        if (File.Exists(MarkerPath(repo, backup.Id, AcceptedSuffix))) return State.Accepted;
        return State.Missing;
    }

    /// <summary>Allows the next run to start a new backup on this drive.</summary>
    internal static void Accept(Backup backup, Destination destination)
    {
        var repo = RepositoryPath(destination) ?? throw new IOException("The drive is not attached.");
        TryDelete(MarkerPath(repo, backup.Id, DeclinedSuffix));
        WriteMarker(MarkerPath(repo, backup.Id, AcceptedSuffix), backup,
            "Backspace may start this backup on this drive. This file is removed after the first successful backup.");
    }

    /// <summary>Remembers on the drive that this backup must not use it.</summary>
    internal static void Decline(Backup backup, Destination destination)
    {
        var repo = RepositoryPath(destination) ?? throw new IOException("The drive is not attached.");
        TryDelete(MarkerPath(repo, backup.Id, AcceptedSuffix));
        WriteMarker(MarkerPath(repo, backup.Id, DeclinedSuffix), backup,
            "Backspace does not back up this backup to this drive. Delete this file, or choose Run now in Backspace, to use the drive again.");
    }

    /// <summary>After a successful backup the repository itself is the proof.</summary>
    internal static void ClearAccepted(Backup backup, Destination destination)
    {
        try
        {
            if (RepositoryPath(destination) is { } repo && HasRepository(repo))
                TryDelete(MarkerPath(repo, backup.Id, AcceptedSuffix));
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
    internal static string SkipReason(State state, Backup backup, Destination destination) => state == State.Declined
        ? $"Skipped: you chose not to back up «{backup.Name}» to the drive now attached for «{destination.Name}». Choose Run now to use it again."
        : $"Waiting for you: the drive attached for «{destination.Name}» has no backup of «{backup.Name}» at {destination.PathOrSubpath}. "
          + "It may be a different drive with the same name, or the drive was erased. Open Backspace and choose Run now to decide.";

    private static void WriteMarker(string path, Backup backup, string note)
    {
        File.WriteAllText(path, $"Backup: {backup.Name}\nBackup ID: {backup.Id}\nWritten: {DateTime.Now:yyyy-MM-dd HH:mm}\n\n{note}\n");
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
