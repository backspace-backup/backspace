using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// The saved log of one check or cleanup (U233, owner, 12 November 2026:
/// "No indication whatsoever about the checks, what it is doing"). Until then
/// a check kept no log: picking it in Activity &amp; logs showed nothing. The
/// file has a run log's header and outcome blocks, so the console draws it
/// like a backup's; each line carries its time. Writing never fails the run.
/// </summary>
internal sealed class MaintenanceRunLog : IDisposable
{
    private static Serilog.ILogger _log => AppLogger.For<MaintenanceRunLog>();
    private readonly object _gate = new();
    private StreamWriter? _writer;

    internal string Path { get; }

    private MaintenanceRunLog(string path) => Path = path;

    /// <summary>Opens the log in the backup's log folder; a log that can't be
    /// opened is skipped and the run goes on.</summary>
    internal static MaintenanceRunLog Open(Backup backup, string operation, IEnumerable<string> destinations, DateTime startedUtc)
    {
        var log = new MaintenanceRunLog(LogStore.NewTranscriptPath(backup.Name, operation, startedUtc));
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log.Path)!);
            log._writer = new StreamWriter(log.Path, append: false) { AutoFlush = true };
            log._writer.WriteLine(ConsoleLogParser.HeaderMarker);
            log._writer.WriteLine($"backup        : {backup.Name}");
            log._writer.WriteLine($"operation     : {operation}");
            log._writer.WriteLine($"destination   : {string.Join(", ", destinations)}");
            log._writer.WriteLine($"started (UTC) : {startedUtc:o}");
            log._writer.WriteLine();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Couldn't open the log of {Operation} for {Backup}", operation, backup.Name);
            log._writer?.Dispose();
            log._writer = null;
        }
        return log;
    }

    internal bool IsOpen => _writer is not null;

    internal void Write(string line)
    {
        if (_writer is null) return;
        try { lock (_gate) _writer?.WriteLine(LogLineStamp.Stamp(line, DateTime.UtcNow)); }
        catch { /* a full disk or a closed file never fails the run */ }
    }

    /// <summary>The outcome block: the run's status and what it says.</summary>
    internal void Outcome(RunRecord record)
    {
        if (_writer is null) return;
        try
        {
            lock (_gate)
            {
                _writer.WriteLine();
                _writer.WriteLine(ConsoleLogParser.OutcomeMarker);
                _writer.WriteLine($"status        : {record.Status}");
                _writer.WriteLine($"summary       : {record.Summary}");
                if (record.Errors.FirstOrDefault() is { Length: > 0 } error)
                    _writer.WriteLine($"last error    : {error}");
                _writer.WriteLine($"ended (UTC)   : {(record.EndedUtc ?? DateTime.UtcNow):o}");
            }
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    /// <summary>"8 min 8 s", "45 s", "1 h 12 min": how long a part took.</summary>
    internal static string Took(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min {span.Seconds} s";
        return $"{Math.Max(0, (int)Math.Round(span.TotalSeconds))} s";
    }

    /// <summary>A check's progress as log lines, not one a second (U233): a
    /// line when its step changes, at each further tenth, or after a minute
    /// without one.</summary>
    internal sealed class ProgressLines
    {
        private readonly Action<string> _say;
        private string _stage = "";
        private double _percent = double.NaN;
        private DateTime _at = DateTime.MinValue;
        private readonly Func<DateTime> _now;

        internal ProgressLines(Action<string> say, Func<DateTime>? now = null) { _say = say; _now = now ?? (() => DateTime.UtcNow); }

        internal void Report(string stage, string text, string where, double percent)
        {
            var now = _now();
            var changed = stage != _stage;
            var further = percent >= 0 && (double.IsNaN(_percent) || Math.Floor(percent / 10) > Math.Floor(_percent / 10));
            var quiet = now - _at >= TimeSpan.FromMinutes(1) && percent != _percent;
            if (!changed && !further && !quiet) return;
            _stage = stage; _percent = percent; _at = now;
            var share = percent >= 0 ? percent.ToString("0", CultureInfo.InvariantCulture) + "% · " : "";
            _say(BackupLiveProgress.Prefix + share + text + (string.IsNullOrEmpty(where) ? "" : " · " + where));
        }
    }
}
