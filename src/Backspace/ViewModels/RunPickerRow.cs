using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using Backspace.Models;
using Backspace.Services;

namespace Backspace.ViewModels;

/// <summary>
/// One entry of Activity &amp; logs' run picker (U232, owner, 12 November 2026:
/// "the Pick a run dropdown is unreadable, please completely revamp it so it
/// is pleasant to use, functional, stunning"). Each run reads as what it was
/// (Backup, Data check (1 GiB sample), Restore check, Cleanup), when ("Today
/// 14:38"), how it ended as a coloured pill with how long it took, and one
/// short line of detail; the whole summary is on hover. A row never wraps.
/// </summary>
public sealed record RunPickerRow(string Title, string When, string Outcome, string Detail, string Tooltip,
    bool IsOk, bool IsWarn, bool IsFail, bool IsRunning)
{
    public static readonly IValueConverter To = new FuncValueConverter<RunRecord?, RunPickerRow?>(r => r is null ? null : From(r));

    public bool HasDetail => Detail.Length > 0;

    private static readonly Regex CheckName = new(@"^(?:Structure check|Data check(?: \([^)]*\))?|Restore check(?: \([^)]*\))?|Sample test restore(?: \([^)]*\))?)",
        RegexOptions.CultureInvariant);
    private static readonly Regex Echo = new(@"^(?:[\s·:.,-]*)(?:passed|completed|finished|succeeded|done)\b[^·]*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static RunPickerRow From(RunRecord r) => From(r, DateTime.Now);

    internal static RunPickerRow From(RunRecord r, DateTime now)
    {
        var running = r is { Status: BackupRunStatus.Running, EndedUtc: null };
        var summary = RunSummaryWording.ForDisplay(r.Summary, r.Status);
        var firstLine = summary.Split('\n', 2)[0].Trim();
        if (firstLine.StartsWith("Running: ", StringComparison.Ordinal)) firstLine = firstLine[9..];
        var title = TitleOf(r, firstLine);
        var took = r.EndedUtc is { } ended ? " · " + MaintenanceRunLog.Took(ended - r.StartedUtc) : "";
        var outcome = running ? "Running" : r.Status switch
        {
            BackupRunStatus.Success => (r.Kind switch
            {
                RunKind.Backup => "Completed",
                RunKind.Prune => "Done",
                RunKind.Restore => "Restored",
                _ => "Passed",
            }) + took,
            BackupRunStatus.Warning => "Needs attention" + took,
            BackupRunStatus.Failed => "Failed" + took,
            BackupRunStatus.Skipped => "Skipped" + took,
            _ => r.Status.ToString(),
        };
        // The detail adds to the title and the pill; it doesn't repeat them.
        var detail = firstLine;
        if (detail.StartsWith(title, StringComparison.OrdinalIgnoreCase)) detail = detail[title.Length..];
        detail = detail.TrimStart(' ', '·', ':', '.', ',', '-');
        if (Echo.IsMatch(detail) || running || string.Equals(detail, r.Status.ToString(), StringComparison.OrdinalIgnoreCase)) detail = "";
        // The pill has the duration already.
        detail = Regex.Replace(detail, @"\s*·\s*in \d+[hms][\w ]*$", "");
        if (detail.Length > 0) detail = char.ToUpper(detail[0], CultureInfo.InvariantCulture) + detail[1..];
        return new RunPickerRow(title, WhenLabel(r.StartedUtc.ToLocalTime(), now), outcome, detail,
            string.IsNullOrWhiteSpace(summary) ? title : summary,
            IsOk: !running && r.Status == BackupRunStatus.Success,
            IsWarn: !running && r.Status is BackupRunStatus.Warning or BackupRunStatus.Skipped,
            IsFail: !running && r.Status == BackupRunStatus.Failed,
            IsRunning: running);
    }

    private static string TitleOf(RunRecord r, string firstLine)
    {
        // The name in the summary gives the size ("Data check (1 GiB sample)"),
        // when it is the same kind of check as the record's.
        if (r.Kind is RunKind.Check or RunKind.TestRestore && CheckName.Match(firstLine) is { Success: true } named)
        {
            var name = named.Value.StartsWith("Sample test restore", StringComparison.Ordinal) ? "Restore check" + named.Value["Sample test restore".Length..] : named.Value;
            var family = r.Kind == RunKind.TestRestore ? CheckKind.TestRestore : r.CheckKind;
            var fits = family switch
            {
                null => true,
                CheckKind.Structure => name == "Structure check",
                CheckKind.DataSample => name.StartsWith("Data check (", StringComparison.Ordinal) && name != "Data check (all data)",
                CheckKind.FullDataRead => name == "Data check (all data)",
                CheckKind.TestRestore => name.StartsWith("Restore check", StringComparison.Ordinal),
                _ => false,
            };
            if (fits) return name;
        }
        return r.Kind switch
        {
            RunKind.Backup => "Backup",
            RunKind.Prune => "Cleanup",
            RunKind.Restore => "Restore",
            RunKind.TestRestore => "Restore check",
            RunKind.Check => r.CheckKind switch
            {
                CheckKind.Structure => "Structure check",
                CheckKind.DataSample => "Data check",
                CheckKind.FullDataRead => "Data check (all data)",
                CheckKind.TestRestore => "Restore check",
                _ => "Check",
            },
            _ => r.Kind.ToString(),
        };
    }

    /// <summary>"Today 14:38", "Yesterday 20:00", "Tue 14:38" this week, then "10 Nov 2026 14:38".</summary>
    internal static string WhenLabel(DateTime local, DateTime now)
    {
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        var days = (now.Date - local.Date).Days;
        return days switch
        {
            0 => "Today " + time,
            1 => "Yesterday " + time,
            > 1 and < 7 => local.ToString("ddd ", CultureInfo.InvariantCulture) + time,
            _ => local.ToString("d MMM yyyy ", CultureInfo.InvariantCulture) + time,
        };
    }
}
