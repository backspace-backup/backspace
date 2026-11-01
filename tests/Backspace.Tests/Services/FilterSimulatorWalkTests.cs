using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Regression for ROADMAP P1 "Filters > Advanced > Simulate fails with
/// exit status 1". SimulateAsync used to shell <c>backup --dry-run</c>
/// through Backrest's RunCommand RPC, which (a) lost Windows backslashes
/// at shlex-tokenize time (C:\ntws → C:ntws → restic exit 1), (b) ran
/// without the plan's excludes so it simulated an UNFILTERED backup,
/// and (c) parsed Duplicacy-era "Packing/Skipped" lines restic never
/// emits, so the pane stayed empty even on success. It is now a local
/// walk sharing FilterTranslator with the engine; these tests pin the
/// walk's contract against a real on-disk tree.
/// </summary>
public class FilterSimulatorWalkTests
{
    private static async Task<(List<SimulatedEntry> Entries, SimulateResult Result)> RunAsync(
        FilterSimulator sim, Backup backup)
    {
        var entries = new List<SimulatedEntry>();
        var result = await sim.SimulateAsync(backup, entries.Add, default);
        return (entries, result);
    }

    private static string MakeTempRoot() =>
        Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "backspace-sim-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Walk_EmitsIncludedAndExcludedWithRule_AndSkipsExcludedSubtrees()
    {
        var root = MakeTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            File.WriteAllText(Path.Combine(root, "keep.txt"), "k");
            File.WriteAllText(Path.Combine(root, "scratch.tmp"), "t");
            File.WriteAllText(Path.Combine(root, "docs", "report.txt"), "r");
            File.WriteAllText(Path.Combine(root, "cache", "junk1.bin"), "j");
            File.WriteAllText(Path.Combine(root, "cache", "junk2.bin"), "j");

            var backup = new Backup
            {
                Name = "sim-walk",
                SourcePaths = { root },
                FiltersText = "*.tmp\ncache/",
            };

            var (entries, result) = await RunAsync(new FilterSimulator(), backup);

            // Included files are emitted with no matching rule.
            var keep = Assert.Single(entries, e => e.Path.EndsWith("keep.txt", StringComparison.Ordinal));
            Assert.True(keep.Included);
            Assert.Null(keep.MatchingRule);
            Assert.Contains(entries, e => e.Included && e.Path.EndsWith("report.txt", StringComparison.Ordinal));

            // An excluded file carries the exact glob that matched it —
            // the paste-into-vanilla-restic string the pane surfaces.
            var tmp = Assert.Single(entries, e => e.Path.EndsWith("scratch.tmp", StringComparison.Ordinal));
            Assert.False(tmp.Included);
            Assert.Equal("*.tmp", tmp.MatchingRule);

            // An excluded directory is emitted ONCE and never entered —
            // restic doesn't descend into excluded dirs, so neither do we.
            var cacheEntries = entries
                .Where(e => e.Path.Contains("cache", StringComparison.OrdinalIgnoreCase)).ToList();
            var dir = Assert.Single(cacheEntries);
            Assert.False(dir.Included);
            Assert.Equal("cache/", dir.MatchingRule);
            Assert.DoesNotContain(entries,
                e => e.Path.Contains("junk", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(entries.Count, result.Entries);
            Assert.False(result.Truncated);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Walk_HonorsMaxEntriesCap()
    {
        var root = MakeTempRoot();
        try
        {
            for (var i = 0; i < 10; i++)
                File.WriteAllText(Path.Combine(root, $"file{i:D2}.txt"), "x");

            var backup = new Backup { Name = "sim-cap", SourcePaths = { root }, FiltersText = "" };
            var sim = new FilterSimulator { MaxEntries = 3 };

            var (entries, result) = await RunAsync(sim, backup);

            Assert.Equal(3, entries.Count);
            Assert.Equal(3, result.Entries);
            Assert.True(result.Truncated);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Walk_MissingSourceYieldsZeroEntriesWithoutThrowing()
    {
        var backup = new Backup
        {
            Name = "sim-missing",
            SourcePaths = { Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")) },
            FiltersText = "*.tmp",
        };

        var (entries, result) = await RunAsync(new FilterSimulator(), backup);

        Assert.Empty(entries);
        Assert.Equal(0, result.Entries);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Walk_WorksWithNoRulesAtAll()
    {
        // The empty-filters case: everything included, nothing throws.
        // (The old engine path required a destination + repo even here.)
        var root = MakeTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "b.txt"), "b");

            var backup = new Backup { Name = "sim-norules", SourcePaths = { root }, FiltersText = "" };
            var (entries, result) = await RunAsync(new FilterSimulator(), backup);

            Assert.Equal(2, entries.Count);
            Assert.All(entries, e => Assert.True(e.Included));
            Assert.False(result.Truncated);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
