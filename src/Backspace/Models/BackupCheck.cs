using System;
using System.Collections.Generic;
using System.Linq;

namespace Backspace.Models;

/// <summary>What one check entry does (U179, 10 November 2026).</summary>
public enum CheckKind
{
    /// <summary><c>restic check</c>: the backup's records are readable and
    /// consistent, and every piece of data they point to is there.</summary>
    Structure,
    /// <summary>Reads part of the stored data, from any backup old or new, and
    /// checks each piece against its fingerprint (restic check with
    /// <c>--read-data-subset</c>). Includes the structure check.</summary>
    DataSample,
    /// <summary>Rebuilds a sample of real files from the latest backup, as a
    /// restore does, optionally comparing them with the originals.</summary>
    TestRestore,
    /// <summary>Reads every piece of stored data (<c>--read-data</c>).</summary>
    FullDataRead,
}

/// <summary>One check, with its own frequency and conditions (U179): "one
/// entry = one check". A backup lists any number of them; cleanup (forget +
/// prune) stays <see cref="Backup.PruneSchedule"/>. Entries due together run
/// one after another, never at once (<see cref="Services.BackupOrchestrator"/>).</summary>
public sealed class BackupCheck
{
    public const long OneGiB = 1024L * 1024 * 1024;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public CheckKind Kind { get; set; }

    /// <summary>When it runs, with its conditions (battery, network, missed
    /// runs). Null: after each backup that reached the destination.</summary>
    public BackupSchedule? Schedule { get; set; }

    /// <summary>Data sample: how much stored data to read, at most (1% of it,
    /// up to this). Test restore: how much to rebuild, at most; 0 means no
    /// limit. Unused by the other kinds.</summary>
    public long MaximumBytes { get; set; } = OneGiB;

    /// <summary>Test restore: share of the latest backup's file sizes to rebuild.</summary>
    public double SamplePercent { get; set; } = 1;

    /// <summary>Test restore: also compare the rebuilt files with today's originals.</summary>
    public bool CompareOriginals { get; set; }

    public BackupCheck Clone() => new()
    {
        Id = Id, Kind = Kind, MaximumBytes = MaximumBytes, SamplePercent = SamplePercent, CompareOriginals = CompareOriginals,
        Schedule = Schedule is null ? null : System.Text.Json.JsonSerializer.Deserialize<BackupSchedule>(System.Text.Json.JsonSerializer.Serialize(Schedule)),
    };

    /// <summary>The test restore this entry asks for.</summary>
    public Services.TestRestoreOptions RestoreOptions() => new(Full: false, SamplePercent, MaximumBytes, CompareOriginals);

    /// <summary>"Structure check", "Structure check + data sample (1 GiB)", "Test restore (1%, at most 1 GiB)".</summary>
    public string Title => Kind switch
    {
        CheckKind.Structure => "Structure check",
        CheckKind.DataSample => $"Structure check + data sample ({Size(MaximumBytes)})",
        CheckKind.TestRestore => MaximumBytes > 0
            ? $"Sample test restore ({SamplePercent:0.##}%, at most {Size(MaximumBytes)})"
            : $"Sample test restore ({SamplePercent:0.##}%)",
        _ => "Full data read",
    };

    /// <summary>The kind's name alone, for the card and run summaries.</summary>
    public static string Name(CheckKind kind) => kind switch
    {
        CheckKind.Structure => "Structure check",
        CheckKind.DataSample => "Structure check + data sample",
        CheckKind.TestRestore => "Sample test restore",
        _ => "Full data read",
    };

    /// <summary>Plain words for a size: "1 GiB", "500 MiB".</summary>
    public static string Size(long bytes) => bytes >= OneGiB
        ? $"{bytes / (double)OneGiB:0.##} GiB"
        : $"{Math.Max(1, bytes / (1024.0 * 1024)):0} MiB";

    /// <summary>The checks of a backup created before checks were a list
    /// (U179). Same commands, same times: the data sample of restic already
    /// checks the structure, so it replaces the structure check rather than
    /// adding to it; the test restore keeps its own options. A full test
    /// restore becomes a 100% sample without a limit.</summary>
    public static List<BackupCheck> FromLegacy(BackupSchedule? schedule, bool readDataSample, bool restoreFiles,
        Services.TestRestoreOptions? restore)
    {
        var checks = new List<BackupCheck>
        {
            new() { Kind = readDataSample ? CheckKind.DataSample : CheckKind.Structure, Schedule = schedule },
        };
        if (restoreFiles)
        {
            var options = restore ?? new();
            checks.Add(new()
            {
                Kind = CheckKind.TestRestore, Schedule = schedule is null ? null : CopyOf(schedule),
                SamplePercent = options.Full ? 100 : options.SamplePercent,
                MaximumBytes = options.Full ? 0 : options.MaximumSampleBytes,
                CompareOriginals = options.CompareOriginals,
            });
        }
        return checks;
    }

    private static BackupSchedule CopyOf(BackupSchedule schedule)
        => System.Text.Json.JsonSerializer.Deserialize<BackupSchedule>(System.Text.Json.JsonSerializer.Serialize(schedule))!;

    /// <summary>The order of checks due together: the cheapest first, so a
    /// broken structure is known before data is downloaded.</summary>
    public static IEnumerable<BackupCheck> InRunOrder(IEnumerable<BackupCheck> checks) => checks.OrderBy(c => c.Kind switch
    {
        CheckKind.Structure => 0, CheckKind.DataSample => 1, CheckKind.FullDataRead => 2, _ => 3,
    });

    /// <summary>"every week", "after each backup".</summary>
    public static string Cadence(BackupSchedule? schedule) => schedule?.Frequency switch
    {
        null => "after each backup",
        ScheduleFrequency.Hourly or ScheduleFrequency.Daily => "every day",
        ScheduleFrequency.Weekly => schedule.WeeklyDaysBitmask == 0b0111_1111 ? "every day" : "every week",
        ScheduleFrequency.Monthly => "every month",
        _ => "every year",
    };

    /// <summary>The one plain sentence Easy mode shows instead of settings
    /// (U179): what Backspace does by itself, grouped by how often.</summary>
    public static string Sentence(Backup backup)
    {
        var groups = new List<(string Cadence, List<string> Things)>();
        void Add(BackupSchedule? schedule, string thing)
        {
            var cadence = Cadence(schedule);
            if (groups.FirstOrDefault(g => g.Cadence == cadence) is { Things: { } things }) things.Add(thing);
            else groups.Add((cadence, new() { thing }));
        }
        Add(backup.PruneSchedule, "removes old copies it no longer needs to keep");
        foreach (var check in InRunOrder(backup.Checks))
            Add(check.Schedule, check.Kind switch
            {
                CheckKind.Structure => "checks that the backup is complete",
                CheckKind.DataSample => $"checks that the backup is complete, reading back {Size(check.MaximumBytes)} of the stored data to make sure it is undamaged",
                CheckKind.TestRestore => "restores a few files as a test to prove they come back",
                _ => "reads back all the stored data to make sure it is undamaged",
            });
        var order = new[] { "after each backup", "every day", "every week", "every month", "every year" };
        groups = groups.OrderBy(g => Array.IndexOf(order, g.Cadence)).ToList();
        var parts = groups.Select((g, i) => (i == 0 ? Capitalise(g.Cadence) + " Backspace " : g.Cadence + " it ") + Join(g.Things));
        return string.Join("; ", parts) + ".";
    }

    private static string Capitalise(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static string Join(IReadOnlyList<string> things) => things.Count switch
    {
        1 => things[0],
        _ => string.Join(", ", things.Take(things.Count - 1)) + " and " + things[^1],
    };
}
