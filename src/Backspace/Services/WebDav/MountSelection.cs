using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.WebDav;

/// <summary>
/// The revisions a mount shows (U162). Only these trees are read, all of them
/// before the drive appears, and nothing else is read afterwards: the owner's
/// log showed Explorer reading every older revision's tree one after another
/// (30-53 s each, up to 12 restic processes each) for four minutes after the
/// drive appeared. Per source, in the backup's order; a source with no chosen
/// revision is left out of the drive.
/// </summary>
public sealed record MountSelection(IReadOnlyList<MountSelection.Source> Sources)
{
    public sealed record Source(string Path, IReadOnlyList<RevisionSummary> Revisions);

    public int RevisionCount => Sources.Sum(s => s.Revisions.Count);

    /// <summary>What the drive shows, for the card: "7 Nov 2026 14:30" for one
    /// revision (or one per source), otherwise "3 revisions".</summary>
    public string Describe()
    {
        var chosen = Sources.Where(s => s.Revisions.Count > 0).ToList();
        if (chosen.Count == 0) return "nothing";
        if (chosen.All(s => s.Revisions.Count == 1))
            return When(chosen.Max(s => s.Revisions[0].CreatedUtc));
        return $"{RevisionCount} revisions";
    }

    /// <summary>"7 Nov 2026 14:30", local time.</summary>
    public static string When(DateTime createdUtc)
        => createdUtc.ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The destination a mount reads: the backup's first target, as
    /// Restore and Browse use.</summary>
    public static (Destination Destination, string StorageName)? FirstTarget(Backup backup)
    {
        if (backup.Targets.Count == 0) return null;
        var target = backup.Targets[0];
        var destination = ServiceLocator.Config.Current.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
        return destination is null ? null : (destination, target.StorageName);
    }

    /// <summary>One source's revisions at the mount's destination, newest first.</summary>
    public static async Task<IReadOnlyList<RevisionSummary>> ListRevisionsAsync(
        Backup backup, string sourcePath, CancellationToken ct)
    {
        if (FirstTarget(backup) is not { } target)
            throw new InvalidOperationException("This backup's first destination is missing from config.");
        using var lease = await ServiceLocator.Maintenance.AcquireReadAsync(ct).ConfigureAwait(false);
        return await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourcePath, target.Destination, target.StorageName, ct).ConfigureAwait(false);
    }

    /// <summary>The latest revision of every source: what a mount shows when
    /// nobody chose (tests, a caller without the picker).</summary>
    public static async Task<MountSelection> LatestAsync(Backup backup, CancellationToken ct)
    {
        var sources = new List<Source>();
        foreach (var path in backup.SourcePaths)
        {
            var revisions = await ListRevisionsAsync(backup, path, ct).ConfigureAwait(false);
            if (revisions.Count > 0) sources.Add(new Source(path, new[] { revisions[0] }));
        }
        return new MountSelection(sources);
    }
}

/// <summary>Where reading a mount's trees has got to: revision
/// <see cref="Index"/> of <see cref="Count"/> (<see cref="Revision"/>, e.g.
/// "7 Nov 2026 14:30"), <see cref="Files"/> of its files read so far, and
/// <see cref="Percent"/> of the whole mount when it can be estimated
/// (<see cref="MountReadEstimate"/>, U166).</summary>
public readonly record struct MountReading(int Files, int Index = 1, int Count = 1, string? Revision = null, int? Percent = null);
