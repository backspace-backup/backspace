using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// How a saved run's summary reads in the app. Runs saved before 4 November
/// 2026 kept the engine's own wording for a run stopped on request ("Backup
/// result: STATUS_USER_CANCELLED. failed to backup: command …" over several
/// lines); the card and the run list showed it as is.
/// </summary>
internal static class RunSummaryWording
{
    internal static string ForDisplay(string? summary, BackupRunStatus status)
    {
        var text = (summary ?? "").Trim();
        if (status == BackupRunStatus.Skipped && text.StartsWith("Backup result: STATUS_USER_CANCELLED", System.StringComparison.Ordinal))
            return "Stopped";
        return text;
    }
}
