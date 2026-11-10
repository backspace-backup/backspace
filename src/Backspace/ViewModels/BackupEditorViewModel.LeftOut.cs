using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Backspace.Services;

namespace Backspace.ViewModels;

/// <summary>
/// Filters › Advanced › Left out (U177, 10 November 2026): what these rules
/// leave out of the backup's folders, shown only once the person runs it.
/// One row per topmost item left out, sorted by size, with the rule
/// responsible (clicking it highlights the rule on the Rules tab), a search
/// box, a total and no cap. Three additions the owner asked for:
///   • By rule: the same result grouped by rule, with each rule's totals.
///   • Compared with the saved rules: while the rules are edited and not
///     saved, rows the edit newly leaves out are marked, and what the saved
///     rules leave out but the edited ones keep is listed as back in.
///   • Keep this: one click adds a <c>!</c> exception for the row at the end
///     of the rules (Save keeps it, like any edit).
/// Rules that match nothing in the backup's folders are listed too (a rule
/// starting with / on Windows says why it never can).
/// </summary>
public sealed partial class BackupEditorViewModel
{
    [ObservableProperty] private bool _leftOutRunning;
    [ObservableProperty] private string _leftOutProgress = "";
    [ObservableProperty] private string _leftOutSearch = "";
    /// <summary>Grouped by rule instead of one row per item.</summary>
    [ObservableProperty] private bool _leftOutByRule;
    [ObservableProperty] private LeftOutResult? _leftOutResult;
    /// <summary>Said instead of a result: cancelled, or why it couldn't run.</summary>
    [ObservableProperty] private string? _leftOutMessage;

    /// <summary>The rows shown: section headings, items or rules, back in,
    /// rules that match nothing, folders that couldn't be read.</summary>
    public ObservableCollection<object> LeftOutRows { get; } = new();

    /// <summary>Asks the window to show and select a rule's line on the Rules tab.</summary>
    public event Action<int>? RuleHighlightRequested;
    /// <summary>The line last highlighted (from 0), for tests.</summary>
    public int HighlightedRuleLine { get; private set; } = -1;

    private CancellationTokenSource? _leftOutCts;
    private string _leftOutRulesRun = "";
    private List<string> _leftOutRoots = new();
    private readonly List<LeftOutRow> _keptRows = new();

    public bool HasLeftOutResult => LeftOutResult is not null;
    public bool LeftOutIdle => !LeftOutRunning;
    public bool LeftOutEmptyState => LeftOutResult is null && !LeftOutRunning && LeftOutMessage is null;

    /// <summary>The rules changed since this result was made.</summary>
    public bool LeftOutStale => LeftOutResult is not null && Norm(FiltersText) != _leftOutRulesRun;

    public string LeftOutTotal
    {
        get
        {
            if (LeftOutResult is not { } r) return "";
            if (r.Items.Count == 0) return "These rules leave nothing out of this backup's folders.";
            var items = r.Items.Count == 1 ? "1 item" : $"{r.Items.Count:N0} items";
            var files = r.TotalFiles == 1 ? "1 file" : $"{r.TotalFiles:N0} files";
            return $"{items} ({files}), {HumanBytes(r.TotalBytes)} left out";
        }
    }

    /// <summary>One line under the total: what was read and what is not counted.</summary>
    public string LeftOutDetail
    {
        get
        {
            if (LeftOutResult is not { } r) return "";
            var parts = new List<string> { $"Read {r.FoldersScanned:N0} folders and {r.FilesScanned:N0} files." };
            if (r.Compared)
            {
                var added = r.Items.Count(i => i.IsNew);
                parts.Add($"Compared with the saved rules: {added:N0} newly left out, {r.BackIn.Count:N0} back in.");
            }
            if (r.CloudOnlySkipped > 0)
                parts.Add(CloudFileBehavior.SkippedPhrase(r.CloudOnlySkipped) + " as well: kept only in the cloud, the backup leaves them out without a rule.");
            return string.Join(" ", parts);
        }
    }

    partial void OnLeftOutResultChanged(LeftOutResult? value) => RefreshLeftOut();
    partial void OnLeftOutSearchChanged(string value) => RebuildLeftOutRows();
    partial void OnLeftOutByRuleChanged(bool value) => RebuildLeftOutRows();
    partial void OnLeftOutRunningChanged(bool value) => RefreshLeftOut();
    partial void OnLeftOutMessageChanged(string? value) => OnPropertyChanged(nameof(LeftOutEmptyState));

    private void RefreshLeftOut()
    {
        foreach (var name in new[] { nameof(HasLeftOutResult), nameof(LeftOutIdle), nameof(LeftOutEmptyState),
                     nameof(LeftOutStale), nameof(LeftOutTotal), nameof(LeftOutDetail) })
            OnPropertyChanged(name);
        RebuildLeftOutRows();
    }

    [RelayCommand] private void ShowLeftOutByItem() => LeftOutByRule = false;
    [RelayCommand] private void ShowLeftOutByRule() => LeftOutByRule = true;

    [RelayCommand]
    private async Task RunLeftOutAsync()
    {
        if (LeftOutRunning) return;
        if (FilterTranslator.InvalidRules(FiltersText) is { Count: > 0 })
        {
            LeftOutResult = null;
            LeftOutMessage = "restic can't read some of these rules (see the Rules tab), so the backup would stop. Fix them, then run this again.";
            return;
        }
        var sources = SourcePaths.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (sources.Count == 0)
        {
            LeftOutResult = null;
            LeftOutMessage = "Add a folder to back up on the Overview tab first.";
            return;
        }
        _leftOutCts?.Dispose();
        _leftOutCts = new CancellationTokenSource();
        var ct = _leftOutCts.Token;
        var rules = FiltersText ?? "";
        var saved = !IsNew && Norm(rules) != Norm(_savedFiltersText) ? _savedFiltersText ?? "" : null;
        var counters = new LeftOutCounters();
        LeftOutMessage = null;
        LeftOutResult = null;
        _keptRows.Clear();
        LeftOutRunning = true;
        LeftOutProgress = "Reading this backup's folders…";
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => LeftOutProgress =
            $"Reading this backup's folders… {Volatile.Read(ref counters.Folders):N0} folders, {Volatile.Read(ref counters.Files):N0} files so far, {HumanBytes(Volatile.Read(ref counters.LeftOutBytes))} left out.";
        timer.Start();
        try
        {
            var result = await ServiceLocator.LeftOut.AnalyzeAsync(
                new LeftOutRequest(sources, rules, saved, ExcludeCloudFiles), counters, ct);
            _leftOutRulesRun = Norm(rules);
            _leftOutRoots = sources.Select(s => { try { return TreeWalk.Root(s); } catch (Exception) { return s; } }).ToList();
            LeftOutResult = result;
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is AggregateException a && a.InnerExceptions.All(e => e is OperationCanceledException))
        {
            LeftOutMessage = Volatile.Read(ref counters.Folders) == 0
                ? "Stopped before reading anything. Run it again to see what these rules leave out."
                : $"Stopped after reading {Volatile.Read(ref counters.Folders):N0} folders and {Volatile.Read(ref counters.Files):N0} files. Run it again to see the whole result.";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Listing what the rules leave out failed");
            LeftOutMessage = "Couldn't read this backup's folders: " + ex.Message;
        }
        finally
        {
            timer.Stop();
            LeftOutRunning = false;
            LeftOutProgress = "";
        }
    }

    [RelayCommand]
    private void CancelLeftOut()
    {
        try { _leftOutCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Shows the rule on the Rules tab, selected. The line is found
    /// again in the current text, in case the rules were edited since.</summary>
    [RelayCommand]
    private void HighlightRule(LeftOutRuleRef? rule)
    {
        if (rule is null) return;
        var lines = (FiltersText ?? "").Split('\n');
        var line = rule.Line >= 0 && rule.Line < lines.Length && lines[rule.Line].Trim() == rule.Rule
            ? rule.Line
            : Array.FindIndex(lines, l => l.Trim() == rule.Rule);
        if (line < 0) return;
        HighlightedRuleLine = line;
        RuleHighlightRequested?.Invoke(line);
    }

    /// <summary>Adds a <c>!</c> exception for the row at the very end of the
    /// rules: the last matching rule decides, so it keeps the item whatever
    /// rule left it out, recommended ones included. Save keeps it.</summary>
    [RelayCommand]
    private void KeepThis(LeftOutRow? row)
    {
        if (row is null || row.Kept) return;
        var rule = KeepRule(row.Item.Path);
        var text = (FiltersText ?? "").TrimEnd('\r', '\n');
        FiltersText = (text.Length == 0 ? "" : text + "\n") + rule + "\n";
        row.Kept = true;
        _keptRows.Add(row);
    }

    /// <summary>The exception that keeps exactly <paramref name="fullPath"/>:
    /// its full path with its drive, glob characters escaped, <c>$</c> doubled.</summary>
    internal static string KeepRule(string fullPath)
    {
        var p = fullPath.Replace('\\', '/').TrimEnd('/');
        var rule = p.Length >= 2 && p[1] == ':'
            ? p[..2] + DuplicacyFilterConverter.EscapeNames(p[2..])
            : DuplicacyFilterConverter.EscapeNames(p);
        return "!" + rule.Replace("$", "$$");
    }

    private void RebuildLeftOutRows()
    {
        LeftOutRows.Clear();
        if (LeftOutResult is not { } r) return;
        var q = LeftOutSearch?.Trim() ?? "";
        bool Hit(string? text) => q.Length == 0 || text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
        // A path is searched below its backed-up folder: the folders above it are in every row.
        bool HitPath(string path) => Hit(_leftOutRoots.FirstOrDefault(top => path.StartsWith(top, StringComparison.OrdinalIgnoreCase)) is { } root
            ? path[root.Length..] : path);

        if (LeftOutByRule)
        {
            foreach (var t in r.ByRule.Where(t => Hit(t.Rule)))
                LeftOutRows.Add(new LeftOutRuleRow(t));
        }
        else
        {
            foreach (var item in r.Items.Where(i => HitPath(i.Path) || Hit(i.Rule)))
                LeftOutRows.Add(_keptRows.FirstOrDefault(k => ReferenceEquals(k.Item, item)) ?? new LeftOutRow(item, Shown(item.Path)));
        }
        if (q.Length > 0 && LeftOutRows.Count == 0)
            LeftOutRows.Add(new LeftOutHeading("Nothing left out matches \"" + q + "\".", null));

        var back = r.BackIn.Where(b => HitPath(b.Path) || Hit(b.SavedRule)).ToList();
        if (back.Count > 0)
        {
            LeftOutRows.Add(new LeftOutHeading("Back in the backup with your edits",
                "The saved rules leave these out; the rules as edited here keep them. Save to make it so."));
            foreach (var b in back) LeftOutRows.Add(new LeftOutBackInRow(b, Shown(b.Path)));
        }

        var own = r.MatchNothing.Where(u => !u.Recommended && Hit(u.Rule)).ToList();
        var recommended = r.MatchNothing.Count(u => u.Recommended && !u.Rooted);
        if (own.Count > 0 || recommended > 0 && q.Length == 0)
        {
            LeftOutRows.Add(new LeftOutHeading("Rules that match nothing here",
                "They leave nothing out of this backup's folders today."));
            foreach (var u in own) LeftOutRows.Add(new LeftOutUnusedRow(u));
            if (recommended > 0 && q.Length == 0)
                LeftOutRows.Add(new LeftOutHeading(null, recommended == 1
                    ? "1 recommended rule matches nothing here today; it stays in case such files appear."
                    : $"{recommended:N0} recommended rules match nothing here today; they stay in case such files appear."));
        }

        var unreadable = r.Unreadable.Where(HitPath).ToList();
        if (unreadable.Count > 0)
        {
            LeftOutRows.Add(new LeftOutHeading("Folders that couldn't be read",
                "Kept by the rules, but Backspace couldn't look inside them, so what they hold isn't counted here. The backup will say whether it can read them."));
            foreach (var path in unreadable) LeftOutRows.Add(new LeftOutPathRow(path, Shown(path)));
        }
    }

    private static string Norm(string? text) => (text ?? "").Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');

    internal static string Size(long bytes) => HumanBytes(bytes);

    /// <summary>A path as a row shows it: from the backed-up folder (its name
    /// first) down, so the rows don't all start with the same long path.</summary>
    private string Shown(string path)
    {
        foreach (var root in _leftOutRoots)
        {
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            var name = System.IO.Path.GetFileName(root.TrimEnd('\\', '/'));
            if (name.Length == 0) return path; // a whole drive: the full path is short already
            var below = path[root.Length..].TrimStart('\\', '/');
            return below.Length == 0 ? name : name + System.IO.Path.DirectorySeparatorChar + below;
        }
        return path;
    }
}

/// <summary>A rule as a row names it, clickable to show it on the Rules tab.</summary>
public sealed record LeftOutRuleRef(string Rule, int Line);

/// <summary>A section heading (or a note) between rows.</summary>
public sealed record LeftOutHeading(string? Title, string? Note)
{
    public bool HasTitle => Title is not null;
    public bool HasNote => Note is not null;
}

/// <summary>One item left out.</summary>
public sealed partial class LeftOutRow : ObservableObject
{
    public LeftOutItem Item { get; }
    public LeftOutRuleRef Rule { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanKeep))] private bool _kept;

    public LeftOutRow(LeftOutItem item, string shown)
    {
        Item = item;
        Rule = new LeftOutRuleRef(item.Rule, item.RuleLine);
        Shown = shown;
    }

    public string Path => Item.Path;
    /// <summary>The path from the backed-up folder down (the full path is the tooltip).</summary>
    public string Shown { get; }
    public string Size => Item.IsLink ? "" : BackupEditorViewModel.Size(Item.Bytes);
    public bool IsNew => Item.IsNew;
    public bool CanKeep => !Kept;

    public string Kind
    {
        get
        {
            if (Item.IsLink) return "Link, not followed: the backup leaves the link itself out";
            var text = Item.IsFolder
                ? (Item.Files == 1 ? "Folder · 1 file" : $"Folder · {Item.Files:N0} files")
                : "File";
            if (Item.CloudOnly > 0)
                text += Item.IsFolder ? $" ({Item.CloudOnly:N0} online-only)" : " · online-only";
            if (Item.Unreadable > 0)
                text += Item.Unreadable == 1 ? " · 1 folder inside couldn't be read, so at least this much"
                    : $" · {Item.Unreadable:N0} folders inside couldn't be read, so at least this much";
            return text;
        }
    }
}

/// <summary>One rule with everything it leaves out.</summary>
public sealed class LeftOutRuleRow(RuleTotal total)
{
    public LeftOutRuleRef Rule { get; } = new(total.Rule, total.Line);
    public string Size => BackupEditorViewModel.Size(total.Bytes);
    public string Detail => (total.Items == 1 ? "1 item" : $"{total.Items:N0} items")
        + " · " + (total.Files == 1 ? "1 file" : $"{total.Files:N0} files");
}

/// <summary>What the saved rules leave out and the edited ones keep.</summary>
public sealed class LeftOutBackInRow(BackInItem item, string shown)
{
    public string Path => item.Path;
    public string Shown => shown;
    public string Size => BackupEditorViewModel.Size(item.Bytes);
    public string Kind => (item.IsFolder ? (item.Files == 1 ? "Folder · 1 file" : $"Folder · {item.Files:N0} files") : "File")
        + (item.SavedRule is { } r ? " · the saved rule " + r + " leaves it out" : "");
}

/// <summary>A rule of the person's that matched nothing.</summary>
public sealed class LeftOutUnusedRow(UnusedRule rule)
{
    public LeftOutRuleRef Rule { get; } = new(rule.Rule, rule.Line);
    public string Why => rule.Rooted
        ? "Matches nothing on Windows: a Windows path starts with its drive, so restic never matches a rule starting with /. Write ?:/ for the top of any drive, or C:/ for one drive."
        : "Matches nothing in this backup's folders.";
}

/// <summary>A folder that couldn't be read.</summary>
public sealed record LeftOutPathRow(string Path, string Shown);
