using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Shows what restic's filter file would actually include / exclude.
///
/// Two modes:
///  • SimulateAsync — runs the engine's enum-only pass and returns the
///    real tree. Authoritative but slower (walks the actual filesystem).
///  • Preview       — runs our own mini glob matcher against a list of
///    candidate paths without touching restic. Instant; useful while
///    typing.
///
/// The preview routes the filter text through <see cref="FilterTranslator"/>
/// first so what it shows matches what the engine actually sends to
/// restic. The "matching rule" string surfaced for each excluded path
/// is the bare restic glob — the same string a user could copy into a
/// vanilla restic <c>--exclude-file</c> and expect to behave the same.
/// </summary>
public sealed class FilterSimulator
{
    private static ILogger _log => AppLogger.For<FilterSimulator>();
    private readonly IBackupEngine _engine;

    public FilterSimulator(IBackupEngine engine) => _engine = engine;

    /// <summary>
    /// Runs a real enum-only pass against every source path of the
    /// backup and feeds results through the callback.
    /// </summary>
    public async Task SimulateAsync(Backup backup, Destination destination, string storageName,
                                    Action<SimulatedEntry> onLine, CancellationToken ct)
    {
        void LineHandler(string raw)
        {
            var entry = ParseEnumLine(raw);
            if (entry is not null) onLine(entry.Value);
        }

        _log.Information("Filter simulation start: backup={Backup} sources={Count} destination={Dest}",
            backup.Name, backup.SourcePaths.Count, destination.Name);

        // Per-call lineSink (not the engine's global LineWritten) so a
        // concurrent backup running elsewhere doesn't bleed its stdout
        // into this preview.
        foreach (var sourcePath in backup.SourcePaths)
        {
            if (ct.IsCancellationRequested) break;
            await _engine.EnumOnlyAsync(backup, sourcePath, destination, storageName, ct, lineSink: LineHandler);
        }
        _log.Information("Filter simulation finished for {Backup}", backup.Name);
    }

    /// <summary>
    /// Synchronous preview: runs the supplied filter text against
    /// <paramref name="candidatePaths"/>. The rule string returned in
    /// <see cref="SimulatedEntry.MatchingRule"/> is the bare restic
    /// glob — paste-into-vanilla-restic-compatible.
    /// </summary>
    public IReadOnlyList<SimulatedEntry> Preview(string filtersText, IEnumerable<string> candidatePaths)
    {
        var translated = FilterTranslator.Translate(filtersText);
        var rules = new List<Rule>(translated.Excludes.Count + translated.IExcludes.Count);
        foreach (var g in translated.Excludes)
            rules.Add(CompileGlob(g, caseInsensitive: false));
        foreach (var g in translated.IExcludes)
            rules.Add(CompileGlob(g, caseInsensitive: true));

        var results = new List<SimulatedEntry>();
        foreach (var p in candidatePaths)
        {
            var (kept, rule) = Evaluate(rules, p);
            results.Add(new SimulatedEntry(p, kept, rule));
        }
        return results;
    }

    private static Rule CompileGlob(string glob, bool caseInsensitive)
    {
        var opts = RegexOptions.Compiled;
        if (caseInsensitive) opts |= RegexOptions.IgnoreCase;
        try
        {
            return new Rule(glob, new Regex(FilterTranslator.GlobToRegex(glob), opts));
        }
        catch
        {
            // Bad pattern → match nothing (user sees their typo as "no effect").
            return new Rule(glob, new Regex("(?!)", RegexOptions.Compiled));
        }
    }

    // ---- engine stdout shape ----

    private static SimulatedEntry? ParseEnumLine(string line)
    {
        var packM = PackRx.Match(line);
        if (packM.Success) return new SimulatedEntry(packM.Groups[1].Value, true, null);

        var skipM = SkipRx.Match(line);
        if (skipM.Success) return new SimulatedEntry(skipM.Groups[1].Value, false, skipM.Groups[2].Value);

        return null;
    }

    private static readonly Regex PackRx = new(@"Packing\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex SkipRx = new(@"Skipped\s+(?:file|directory)\s+(.+?)(?:\s+\(filter rule:\s*(.+?)\))?$",
        RegexOptions.Compiled);

    // ---- preview engine ----

    /// <summary>Compiled exclude rule. <see cref="Glob"/> is the original
    /// restic pattern (what we surface to the user as "matched by");
    /// <see cref="Pattern"/> is the equivalent compiled regex.</summary>
    private readonly record struct Rule(string Glob, Regex Pattern);

    private static (bool included, string? rule) Evaluate(IReadOnlyList<Rule> rules, string path)
    {
        // restic semantics: any matching exclude rule excludes; default included.
        foreach (var r in rules)
        {
            if (r.Pattern.IsMatch(path)) return (false, r.Glob);
        }
        return (true, null);
    }

    /// <summary>
    /// Thin pass-through to <see cref="FilterTranslator.GlobToRegex"/>
    /// — kept so the existing FilterSimulatorGlobTests don't need to
    /// switch namespace. The single canonical implementation lives on
    /// FilterTranslator.
    /// </summary>
    internal static string GlobToRegex(string glob) => FilterTranslator.GlobToRegex(glob);
}

public readonly record struct SimulatedEntry(string Path, bool Included, string? MatchingRule);
