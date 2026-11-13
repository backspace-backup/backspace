using System;
using System.Linq;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// What the command line prints while the background service runs its
/// command (U180, owner go-ahead, 13 November 2026): the run's own readable
/// lines, as Activity &amp; logs shows them without Verbose (each check or
/// backup as a heading, then its steps and its result, indented), and the
/// run's summary at the end. Lines are taken from the service's live
/// transcript, each printed once.
/// </summary>
internal sealed class CommandLineTranscript(string backupId, DateTime startedUtc, Action<string> write)
{
    private readonly ConsoleLogParser _parser = new();
    private long _printed = -1;
    private bool _summaryShown;

    /// <summary>Prints the lines the run wrote since the last call.</summary>
    internal void Show(ServiceActivity.Snapshot? activity)
    {
        if (Ours(activity) is not { Progress: { } progress }) return;
        var tail = progress.TranscriptTail ?? "";
        var start = progress.TranscriptEnd - tail.Length;
        if (_printed < start) _printed = start; // older lines left the window
        var from = (int)Math.Min(tail.Length, Math.Max(0, _printed - start));
        var chunk = tail[from..];
        var end = chunk.LastIndexOf('\n');
        if (end < 0) return; // only whole lines
        chunk = chunk[..(end + 1)];
        _printed = start + from + chunk.Length;
        foreach (var raw in chunk.Split('\n'))
        {
            if (_parser.Feed(raw.TrimEnd('\r'), null) is not { } line || line.IsDetail || line.Kind == ConsoleLineKind.Blank) continue;
            write(Format(line));
        }
    }

    /// <summary>The finished run's summary, once.</summary>
    internal void ShowSummary(ServiceActivity.Snapshot? activity)
    {
        if (_summaryShown || Ours(activity)?.Progress?.Result is not { } result || string.IsNullOrWhiteSpace(result.Summary)) return;
        _summaryShown = true;
        foreach (var line in result.Summary.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            write("=> " + line);
    }

    private ServiceActivity.Snapshot? Ours(ServiceActivity.Snapshot? activity)
    {
        var run = ServiceActivity.ForBackup(activity, backupId);
        // This command's run, not one that ended before it started.
        return run is { BackupId: { } id, StartedUtc: { } started } && id == backupId && started >= startedUtc.AddSeconds(-5) ? run : null;
    }

    internal static string Format(ConsoleLine line)
    {
        if (line.Kind == ConsoleLineKind.Section)
            return line.Subtitle is { Length: > 0 } where ? $"{line.Text} · {where}" : line.Text;
        var text = line.Text;
        // "[recap] Data check passed…" reads as "Data check passed…".
        if (text.StartsWith('[') && text.IndexOf("] ", StringComparison.Ordinal) is var close and > 0 and < 16) text = text[(close + 2)..];
        return "  " + text;
    }
}
