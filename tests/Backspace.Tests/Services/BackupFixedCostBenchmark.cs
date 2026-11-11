using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Manual timing of what a backup costs besides restic's own backup (U175):
/// an unchanged source backed up again and again into a local repository
/// through the real orchestrator and engine, as a scheduled run does, once
/// without and once with the cleanup after each backup (checks are entries of
/// their own since U179; the check inside a backup is not timed). Each run's
/// own log gives the time before restic's backup starts, restic's backup, and
/// the time from its end to the next step. Also times starting and stopping
/// the Backrest daemon, which each background-service run did once.
/// Set BACKSPACE_FIXED_COST_BENCH to a folder (outside any synced folder) for
/// the fixture and results.txt; BACKSPACE_FIXED_COST_BENCH_RUNS (default 8)
/// and BACKSPACE_FIXED_COST_BENCH_LABEL are optional. Skipped otherwise.
/// </summary>
public sealed class BackupFixedCostBenchmark(ITestOutputHelper output)
{
    [Fact]
    public async Task Time_the_fixed_cost_of_unchanged_backups()
    {
        var root = Environment.GetEnvironmentVariable("BACKSPACE_FIXED_COST_BENCH");
        if (string.IsNullOrEmpty(root)) Assert.Skip("Set BACKSPACE_FIXED_COST_BENCH to a folder to time the fixed cost of a backup.");
        var runs = int.TryParse(Environment.GetEnvironmentVariable("BACKSPACE_FIXED_COST_BENCH_RUNS"), out var n) && n > 0 ? n : 8;
        await using var fixture = await RealEngineFixture.TryCreateAsync(output);
        if (fixture is null) Assert.Skip("Engines missing.");
        var work = Path.Combine(root!, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        var source = Path.Combine(work, "source");
        var random = new Random(175);
        for (var folder = 0; folder < 12; folder++)
        {
            var directory = Path.Combine(source, $"folder {folder:00}");
            Directory.CreateDirectory(directory);
            for (var file = 0; file < 20; file++)
            {
                var bytes = new byte[16 * 1024 + random.Next(8 * 1024)];
                random.NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(directory, $"file {file:00}.bin"), bytes);
            }
        }
        var report = new StringBuilder();
        void Note(string line) { output.WriteLine(line); report.AppendLine(line); }
        Note($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.GetEnvironmentVariable("BACKSPACE_FIXED_COST_BENCH_LABEL")} ({runs} runs each, 240 files, local repository)");

        foreach (var (name, inline) in new[] { ("backup only", false), ("backup and cleanup", true) })
        {
            var destination = new Destination { Name = "Bench " + name, Kind = DestinationKind.LocalFolder,
                PathOrSubpath = Path.Combine(work, inline ? "repo-inline" : "repo"), Encrypted = true,
                StoragePasswordRef = ServiceLocator.Secrets.PutNew("u175-bench", Guid.NewGuid().ToString("N")) };
            var backup = new Backup { Name = "Fixed cost " + (inline ? "inline" : "alone") + " " + Guid.NewGuid().ToString("N")[..6],
                SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true, AbortOnMeteredNetwork = false,
                PruneSchedule = inline ? null : new BackupSchedule(), CheckSchedule = new BackupSchedule(), // no check inside the backup
                Targets = { new BackupTarget { DestinationId = destination.Id, StorageName = "default" } } };
            ServiceLocator.Config.Update(c => { c.Destinations.Add(destination); c.Backups.Add(backup); });
            // The first backup creates the repository and reads everything: not counted.
            var first = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
            Assert.True(first.Status == BackupRunStatus.Success, first.Summary + " " + string.Join(" | ", first.Errors));
            var totals = new List<double>(); var cells = new List<double>(); var before = new List<double>();
            var resticBackup = new List<double>(); var after = new List<double>(); var maintenance = new List<double>();
            for (var i = 0; i < runs; i++)
            {
                var watch = Stopwatch.StartNew();
                var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
                watch.Stop();
                Assert.True(record.Status == BackupRunStatus.Success, record.Summary + " " + string.Join(" | ", record.Errors));
                totals.Add(watch.Elapsed.TotalSeconds);
                var phases = Phases(record.LogPath);
                if (phases.Cell is { } c) cells.Add(c);
                if (phases.Before is { } b) before.Add(b);
                if (phases.Restic is { } r) resticBackup.Add(r);
                if (phases.After is { } a) after.Add(a);
                if (phases.Maintenance is { } m) maintenance.Add(m);
            }
            Note($"{name}: whole run {Stats(totals)}; its log from start to end {Stats(cells)}");
            Note($"  before restic's backup starts {Stats(before)}; restic's backup {Stats(resticBackup)}; from its end to the next step {Stats(after)}"
                + (inline ? $"; cleanup {Stats(maintenance)}" : ""));
        }

        File.AppendAllText(Path.Combine(root!, "results.txt"), report.ToString());
    }

    private static string Stats(List<double> values)
    {
        if (values.Count == 0) return "n/a";
        var sorted = values.OrderBy(v => v).ToArray();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        return string.Create(CultureInfo.InvariantCulture, $"median {median:0.00} s (min {sorted[0]:0.00}, max {sorted[^1]:0.00})");
    }

    private static readonly Regex Inner = new(@"^\[[\w-]+\] (?<t>\d\d:\d\d:\d\d\.\d+)Z\s", RegexOptions.CultureInvariant);
    private static readonly Regex BackupCommand = new(@"command: ""?[^""]*restic(?:\.exe)?""? backup ", RegexOptions.CultureInvariant);
    private static readonly Regex BackupEnd = new(@"\s(?:debug|info|warn|error)\s+backup (?:completed|finished)", RegexOptions.CultureInvariant);

    /// <summary>Seconds of each part of a run, from its log: the engine's
    /// own lines carry the time they happened (Backrest wrote local time
    /// marked "Z"; the offset is found from the log's UTC header).</summary>
    internal static (double? Cell, double? Before, double? Restic, double? After, double? Maintenance) Phases(string? log)
    {
        if (string.IsNullOrEmpty(log) || !File.Exists(log)) return default;
        var lines = File.ReadAllLines(log);
        DateTime? Header(string key)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal));
            return line is null ? null : DateTime.Parse(line[(line.IndexOf(" : ", StringComparison.Ordinal) + 3)..].Trim(),
                CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        var start = Header("started (UTC)"); var end = Header("ended (UTC)");
        if (start is null || end is null) return default;
        TimeSpan? InnerOf(string line)
        {
            var rest = LogLineStamp.Strip(line);
            var m = Inner.Match(rest);
            return m.Success ? TimeSpan.ParseExact(m.Groups["t"].Value, @"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture) : null;
        }
        DateTime? Outer(string line) => LogLineStamp.TryParse(line, out var utc, out _) ? utc : null;
        var commandIndex = Array.FindIndex(lines, l => BackupCommand.IsMatch(l));
        var endIndex = Array.FindIndex(lines, l => BackupEnd.IsMatch(l));
        if (commandIndex < 0 || endIndex < 0 || InnerOf(lines[commandIndex]) is not { } commandTime || InnerOf(lines[endIndex]) is not { } endTime)
            return ((end - start)!.Value.TotalSeconds, null, null, null, null);
        // Backrest's clock was local time labelled Z: find the whole half-hours.
        var offset = TimeSpan.FromMinutes(Math.Round((commandTime - start.Value.TimeOfDay).TotalMinutes / 30) * 30);
        DateTime At(TimeSpan inner)
        {
            var at = start.Value.Date + inner - offset;
            while (at < start.Value.AddHours(-12)) at = at.AddDays(1);
            while (at > start.Value.AddHours(12)) at = at.AddDays(-1);
            return at;
        }
        var resticStart = At(commandTime); var resticEnd = At(endTime);
        var cleanupIndex = Array.FindIndex(lines, l => LogLineStamp.Strip(l).StartsWith(LogSection.Prefix, StringComparison.Ordinal)
            && (l.Contains("· cleanup", StringComparison.Ordinal) || l.Contains("· check", StringComparison.Ordinal)));
        var next = cleanupIndex >= 0 && Outer(lines[cleanupIndex]) is { } maintenanceStart ? maintenanceStart : end.Value;
        return ((end - start)!.Value.TotalSeconds, (resticStart - start.Value).TotalSeconds, (resticEnd - resticStart).TotalSeconds,
            (next - resticEnd).TotalSeconds, cleanupIndex >= 0 ? (end.Value - next).TotalSeconds : null);
    }
}
