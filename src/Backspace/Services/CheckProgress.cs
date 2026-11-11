using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Backspace.Services;

/// <summary>What a running check is doing (U198, 11 November 2026): the
/// step, with restic's count when it gives one ("12 of 64 packs").</summary>
public sealed record CheckStep(string Stage, long Done = 0, long Total = 0, string Unit = "")
{
    public string Text => Total > 0
        ? $"{Stage} · {Done.ToString("N0", CultureInfo.CurrentCulture)} of {Total.ToString("N0", CultureInfo.CurrentCulture)} {Unit}"
        : Stage;
}

/// <summary>Reads restic's plain <c>check</c> output, with its progress lines
/// (RESTIC_PROGRESS_FPS), into steps and a percentage of the whole check on
/// one destination. restic counts index files, snapshots and packs, not
/// time: each step gets a share of the bar (reading data, when the check
/// reads any, the largest), so the bar only moves forward.</summary>
public sealed class CheckProgressReader
{
    private static readonly Regex Bar = new(@"^\[[0-9:]+\]\s+[0-9.]+%\s+([0-9]+)\s*/\s*([0-9]+)\s+(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly bool _readsData;
    private CheckStep _step = new("Preparing");
    private double _percent;
    private (double From, double To) _range = (0, 0);

    /// <param name="readsData">The check reads stored data (a data sample or
    /// all of it) after the structure check.</param>
    public CheckProgressReader(bool readsData) { _readsData = readsData; }

    public CheckStep Step => _step;
    /// <summary>0..100, for this check on this destination.</summary>
    public double Percent => _percent;

    /// <summary>restic's progress lines: "[0:05] 33.33%  1 / 3 packs".</summary>
    public static bool IsProgressLine(string line) => Bar.IsMatch(line);

    /// <summary>True when the line moved the check on.</summary>
    public bool Read(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return false;
        var bar = Bar.Match(line);
        if (bar.Success)
        {
            if (!long.TryParse(bar.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var done)
                || !long.TryParse(bar.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total)) return false;
            var unit = bar.Groups[3].Value;
            // The unit says which step counts, whatever line came before.
            if (unit.StartsWith("snapshot", StringComparison.OrdinalIgnoreCase) && _step.Stage != SnapshotsStage) Enter(SnapshotsStage);
            else if (unit.StartsWith("index", StringComparison.OrdinalIgnoreCase) && _step.Stage != IndexStage) Enter(IndexStage);
            _step = _step with { Done = done, Total = total, Unit = unit };
            if (total > 0) Advance(_range.From + (_range.To - _range.From) * Math.Clamp(done / (double)total, 0, 1));
            return true;
        }
        if (line.StartsWith("load indexes", StringComparison.OrdinalIgnoreCase)) return Enter(IndexStage);
        if (line.StartsWith("check all packs", StringComparison.OrdinalIgnoreCase)) return Enter(PacksStage);
        if (line.StartsWith("check snapshots", StringComparison.OrdinalIgnoreCase)) return Enter(SnapshotsStage);
        if (line.StartsWith("read all data", StringComparison.OrdinalIgnoreCase)) { _readingAll = true; return Enter(ReadingStage); }
        if (line.StartsWith("read ", StringComparison.OrdinalIgnoreCase) && line.Contains("data", StringComparison.OrdinalIgnoreCase)) return Enter(ReadingStage);
        if (line.StartsWith("no errors were found", StringComparison.OrdinalIgnoreCase)) { Advance(100); _step = new("Finishing"); return true; }
        return false;
    }

    private bool _readingAll;
    internal const string IndexStage = "Loading the index";
    internal const string PacksStage = "Checking the stored packs";
    internal const string SnapshotsStage = "Checking the snapshots";
    internal const string ReadingStage = "Reading stored data";

    private bool Enter(string stage)
    {
        _range = (_readsData, stage) switch
        {
            (false, IndexStage) => (0, 10),
            (false, PacksStage) => (10, 20),
            (false, SnapshotsStage) => (20, 100),
            (true, IndexStage) => (0, 5),
            (true, PacksStage) => (5, 10),
            (true, SnapshotsStage) => (10, 30),
            (true, ReadingStage) => (30, 100),
            _ => (_percent, _percent),
        };
        _step = new(stage == ReadingStage ? (_readingAll ? "Reading all stored data" : "Reading the data sample") : stage);
        Advance(_range.From);
        return true;
    }

    private void Advance(double percent) => _percent = Math.Max(_percent, Math.Clamp(percent, 0, 100));
}
