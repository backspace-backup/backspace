using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Backspace.Services;

/// <summary>The pinned daemon exposes completion counters for its otherwise
/// invisible background index task. A counter alone never identifies a
/// snapshot; the caller also requires its exact indexed snapshot record.</summary>
internal readonly record struct BackrestIndexCounters(long Success, long Failed)
{
    internal static BackrestIndexCounters Parse(string text, string repoId)
    {
        long success = 0, failed = 0;
        var repoLabel = "repo_id=" + JsonSerializer.Serialize(repoId);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("backrest_tasks_run_total{", StringComparison.Ordinal)) continue;
            var end = line.LastIndexOf('}');
            if (end < 0) throw new InvalidDataException("Invalid engine task counter.");
            var labels = "," + line["backrest_tasks_run_total{".Length..end] + ",";
            if (!labels.Contains("," + repoLabel + ",", StringComparison.Ordinal) ||
                !labels.Contains(",task_type=\"index_snapshots\",", StringComparison.Ordinal)) continue;
            if (!double.TryParse(line[(end + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var count) ||
                !double.IsFinite(count) || count < 0 || count >= long.MaxValue || count != Math.Truncate(count))
                throw new InvalidDataException("Invalid engine task counter value.");
            if (labels.Contains(",status=\"success\",", StringComparison.Ordinal)) success = checked(success + (long)count);
            else if (labels.Contains(",status=\"failed\",", StringComparison.Ordinal)) failed = checked(failed + (long)count);
        }
        return new(success, failed);
    }
}
