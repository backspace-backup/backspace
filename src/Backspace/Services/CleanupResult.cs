using System.Globalization;
using System.Text.RegularExpressions;

namespace Backspace.Services;

/// <summary>
/// What a cleanup removed (U142, 6 November 2026): the old backups the keep
/// rules no longer keep, and the storage that freed. Read from restic's own
/// output of <c>forget</c> and <c>prune</c>, so users can see why the storage
/// doesn't only grow.
/// </summary>
internal readonly record struct CleanupResult(int SnapshotsRemoved, long BytesFreed)
{
    internal const string Prefix = "[cleanup] ";

    // forget: "remove 2 snapshots:" before listing them, and with --prune
    // "2 snapshots have been removed, running prune" once they are gone.
    private static readonly Regex Listed = new(@"^remove (?<n>\d+) snapshots:", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex Removed = new(@"^(?<n>\d+) snapshots have been removed", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    // prune: "total prune:  74 blobs / 1.072 MiB" (restic's ui.FormatBytes).
    private static readonly Regex TotalPrune = new(@"^total prune:\s+\d+ blobs / (?<size>[0-9]+(?:\.[0-9]+)?) (?<unit>B|KiB|MiB|GiB|TiB)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal static CleanupResult Parse(string? output)
    {
        if (string.IsNullOrEmpty(output)) return default;
        output = output.Replace("\r\n", "\n");
        var removed = Sum(Removed, output);
        if (removed == 0) removed = Sum(Listed, output);
        long freed = 0;
        foreach (Match m in TotalPrune.Matches(output))
        {
            var size = double.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups["unit"].Value switch { "KiB" => 1L << 10, "MiB" => 1L << 20, "GiB" => 1L << 30, "TiB" => 1L << 40, _ => 1L };
            freed += (long)System.Math.Round(size * unit);
        }
        return new(removed, freed);
    }

    private static int Sum(Regex pattern, string output)
    {
        var total = 0;
        foreach (Match m in pattern.Matches(output))
            if (int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) total += n;
        return total;
    }

    internal CleanupResult Plus(CleanupResult other) => new(SnapshotsRemoved + other.SnapshotsRemoved, BytesFreed + other.BytesFreed);

    /// <summary>The log's line for this cleanup.</summary>
    internal string Recap()
    {
        if (SnapshotsRemoved == 0 && BytesFreed == 0)
            return Prefix + "Nothing to remove: the keep rules keep every backup.";
        var parts = new System.Collections.Generic.List<string>();
        if (SnapshotsRemoved > 0)
            parts.Add(SnapshotsRemoved == 1 ? "1 old backup removed" : $"{SnapshotsRemoved.ToString("N0", CultureInfo.InvariantCulture)} old backups removed");
        parts.Add(BytesFreed > 0 ? $"{BackupLiveProgress.Bytes(BytesFreed)} freed in the storage" : "no storage freed: what they held is still used by the backups kept");
        return Prefix + string.Join(" · ", parts);
    }
}
