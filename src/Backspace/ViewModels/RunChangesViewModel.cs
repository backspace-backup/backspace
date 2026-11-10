using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backspace.ViewModels;

/// <summary>One of the largest files a backup added or changed.</summary>
public sealed record ChangedFileRow(string Name, string Folder, string Size, string Change);

/// <summary>
/// What one backup added or changed (U143, 5 November 2026): a treemap of the
/// folders the changed files are in, sized by their bytes, like WinDirStat,
/// and the largest files. Read from the backup's snapshot and the one before.
/// </summary>
public sealed partial class RunChangesViewModel : ObservableObject, IDisposable
{
    private readonly Backup _backup;
    private readonly RunRecord _run;
    private readonly CancellationTokenSource _cts = new();

    public string Title => "What this backup added";
    public string Subtitle { get; }

    [ObservableProperty] private string _status = "Starting…";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _hoverText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Location), nameof(CanGoUp))] private ChangeNode? _current;
    private ChangeNode? _top;
    private IReadOnlyList<string> _sources = Array.Empty<string>();

    public ObservableCollection<ChangedFileRow> LargestFiles { get; } = new();
    public bool HasFiles => LargestFiles.Count > 0;

    /// <summary>Where the map is, from the top of the changes.</summary>
    public string Location => Current is null || _top is null ? "" : Relative(Current.Path);

    /// <summary>The folder the map starts at, in full, for its tooltip.</summary>
    public string TopFolder => _top is null ? "" : _top.Path.Replace('/', System.IO.Path.DirectorySeparatorChar);

    // A path from the top of the map, which is named by its last folder
    // ("profile\Projects\app"): the full path is long and the same everywhere.
    private string Relative(string path)
    {
        if (_top is null) return path;
        var rest = path.Length > _top.Path.Length ? path[(_top.Path.Length + (_top.Path.Length > 0 ? 1 : 0))..] : "";
        return (_top.Name + (rest.Length > 0 ? "/" + rest : "")).Replace('/', System.IO.Path.DirectorySeparatorChar);
    }
    public bool CanGoUp => Current is not null && Current != _top && Current.Parent is not null;

    internal RunChangesViewModel(Backup backup, RunRecord run)
    {
        _backup = backup;
        _run = run;
        Subtitle = $"{backup.Name} · {run.StartedUtc.ToLocalTime():ddd d MMM yyyy · HH:mm}";
    }

    [RelayCommand]
    private void Up()
    {
        if (!CanGoUp) return;
        // Back past folders holding a single folder, as Open skipped them.
        var node = Current!.Parent!;
        while (node != _top && node.Parent is not null && node.Children.Count() == 1) node = node.Parent;
        Current = node;
    }

    public void Open(ChangeNode node)
    {
        // Through folders holding a single folder, to where the changes branch out.
        if (!node.IsFile && node.HasChildren) Current = node.Collapsed();
    }

    public void Hover(ChangeNode? node) => HoverText = node is null ? ""
        : $"{Relative(node.Path)} · {BackupLiveProgress.Bytes(node.Bytes)}"
          + (node.IsFile ? (node.IsNew ? " · new" : " · changed") : $" · {node.Files.ToString("N0", CultureInfo.InvariantCulture)} files");

    /// <summary>Reads and compares the snapshots of each source this run backed up.</summary>
    public async Task LoadAsync(IBackupEngine engine)
    {
        var ct = _cts.Token;
        try
        {
            var config = ServiceLocator.Config.Current;
            // One destination per source is enough: every destination got the same files.
            var cells = _run.Cells.Where(c => c.Status is BackupRunStatus.Success or BackupRunStatus.Warning)
                .GroupBy(c => c.SourcePath, StringComparer.OrdinalIgnoreCase).Select(g => g.ToList()).ToList();
            if (cells.Count == 0)
            {
                Finish("This run made no snapshot to compare.");
                return;
            }
            var all = new List<ChangedFile>();
            var missing = new List<string>();
            var progress = new Progress<string>(s => Status = s);
            foreach (var group in cells)
            {
                RunChanges.Result? result = null;
                foreach (var cell in group)
                {
                    var destination = config.Destinations.FirstOrDefault(d => string.Equals(d.Name, cell.DestinationName, StringComparison.OrdinalIgnoreCase));
                    if (destination is null || await Task.Run(() => RunChanges.SnapshotOf(_run, cell), ct) is not { } snapshot) continue;
                    result = await RunChanges.LoadAsync(engine, _backup, cell.SourcePath, destination, cell.StorageName, snapshot, progress, ct);
                    if (result is not null) break;
                }
                var source = group[0].SourcePath;
                if (result is null) { missing.Add(source); continue; }
                var prefix = source.Replace('\\', '/').TrimEnd('/');
                all.AddRange(result.Files.Select(f => f with { Path = prefix + "/" + f.Path }));
                if (result.PreviousId is null) missing.Add(source + " (first backup: every file is new)");
            }
            _sources = cells.Select(g => g[0].SourcePath).ToList();
            var tree = await Task.Run(() => ChangeNode.Build(_backup.Name, all), ct);
            _top = tree.Collapsed();
            Current = _top;
            OnPropertyChanged(nameof(TopFolder));
            var largest = all.OrderByDescending(f => f.Size).Take(200).Select(f =>
            {
                var slash = f.Path.LastIndexOf('/');
                return new ChangedFileRow(f.Path[(slash + 1)..], Relative(f.Path[..Math.Max(0, slash)]),
                    BackupLiveProgress.Bytes(f.Size), f.IsNew ? "new" : "changed");
            });
            foreach (var row in largest) LargestFiles.Add(row);
            OnPropertyChanged(nameof(HasFiles));
            var c = CultureInfo.InvariantCulture;
            Summary = $"{all.Count(f => f.IsNew).ToString("N0", c)} new · {all.Count(f => !f.IsNew).ToString("N0", c)} changed · {BackupLiveProgress.Bytes(all.Sum(f => f.Size))} of files";
            var notes = new List<string>();
            if (all.Count == 0) notes.Add("No file was added or changed.");
            var unavailable = missing.Where(m => !m.Contains("first backup", StringComparison.Ordinal)).ToList();
            if (unavailable.Count > 0) notes.Add("Not shown, its snapshot is no longer kept or its log has no recap: " + string.Join(", ", unavailable) + ".");
            var first = missing.Where(m => m.Contains("first backup", StringComparison.Ordinal)).ToList();
            if (first.Count > 0) notes.Add(string.Join(", ", first) + ".");
            Finish(string.Join(" ", notes));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Finish("The file lists couldn't be read: " + ex.Message);
        }
    }

    private void Finish(string status)
    {
        Status = status;
        IsLoading = false;
    }

    // ---------------------------------------------------------------
    // Exclude this folder (U178, 10 November 2026): beside the path, a
    // button that adds a rule leaving the opened folder out of this
    // backup's next runs, with a confirmation that shows the rule, and Undo.
    // ---------------------------------------------------------------

    /// <summary>The opened folder, in full, when it can be excluded: a folder
    /// inside one of the backed-up folders (not one of them, nor above).</summary>
    [ObservableProperty] private string? _excludePath;
    /// <summary>"Exclude this folder" is offered.</summary>
    [ObservableProperty] private bool _canExclude;
    [ObservableProperty] private bool _isConfirmingExclude;
    /// <summary>The rule this window added for the opened folder is saved.</summary>
    [ObservableProperty] private bool _isExcluded;
    /// <summary>Set when an existing rule already leaves the opened folder out.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAlreadyExcluded))] private string _alreadyExcludedBy = "";
    public bool IsAlreadyExcluded => AlreadyExcludedBy.Length > 0;
    [ObservableProperty] private string _excludeRule = "";
    [ObservableProperty] private string _excludeQuestion = "";
    [ObservableProperty] private string _excludeRuleIntro = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasExcludeOnDisk))] private string _excludeOnDisk = "";
    public bool HasExcludeOnDisk => ExcludeOnDisk.Length > 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasExcludeWarning), nameof(CanConfirmExclude))] private string _excludeWarning = "";
    public bool HasExcludeWarning => ExcludeWarning.Length > 0;
    public bool CanConfirmExclude => ExcludeWarning.Length == 0;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasExcludeError))] private string _excludeError = "";
    public bool HasExcludeError => ExcludeError.Length > 0;

    /// <summary>What this window added, by folder: the rule and the filters
    /// before and after, so Undo can put back exactly what was there.</summary>
    private readonly Dictionary<string, (string Rule, string Before, string After)> _excluded = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _measureCts;

    partial void OnCurrentChanged(ChangeNode? value) => RefreshExclusion();

    private Backup? SavedBackup() => ServiceLocator.Config.Current.Backups.FirstOrDefault(b => b.Id == _backup.Id);

    /// <summary>The folder <paramref name="node"/> stands for on this computer,
    /// or null when it is a backed-up folder itself or above one.</summary>
    internal string? FullPathOf(ChangeNode? node)
    {
        if (node is null || node.IsFile) return null;
        string? best = null, rest = null;
        foreach (var source in _sources)
        {
            var key = source.Replace('\\', '/').Trim('/');
            string? tail = key.Length == 0 ? node.Path
                : node.Path.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase) ? node.Path[(key.Length + 1)..] : null;
            if (tail is null || tail.Length == 0 || (best is not null && best.Length >= source.Length)) continue;
            (best, rest) = (source, tail);
        }
        if (best is null || rest is null) return null;
        var root = best.TrimEnd('/', '\\');
        var sep = System.IO.Path.DirectorySeparatorChar;
        return (root.Length == 0 ? sep.ToString() : root + sep) + rest.Replace('/', sep);
    }

    private void RefreshExclusion()
    {
        CancelMeasure();
        IsConfirmingExclude = false;
        ExcludeError = "";
        var path = FullPathOf(Current);
        var backup = path is null ? null : SavedBackup();
        var rule = path is null ? null : FolderExclusion.Rule(path);
        ExcludePath = backup is null || rule is null ? null : path;
        ExcludeRule = rule ?? "";
        if (ExcludePath is null)
        {
            (CanExclude, IsExcluded, AlreadyExcludedBy) = (false, false, "");
            return;
        }
        var filters = backup!.FiltersText ?? "";
        IsExcluded = _excluded.TryGetValue(ExcludePath, out var added)
            && FilterTranslator.ReadRules(filters).Any(r => string.Equals(r.Line, added.Rule, StringComparison.OrdinalIgnoreCase));
        AlreadyExcludedBy = IsExcluded ? "" : FolderExclusion.ExcludedBy(filters, ExcludePath) ?? "";
        CanExclude = !IsExcluded && !IsAlreadyExcluded;
    }

    /// <summary>Shows the confirmation: the rule, what the folder holds now and
    /// that earlier backups keep it.</summary>
    [RelayCommand]
    private async Task ExcludeFolder()
    {
        if (!CanExclude || ExcludePath is not { } path || SavedBackup() is not { } backup) return;
        var name = Current?.Name ?? System.IO.Path.GetFileName(path);
        ExcludeQuestion = $"Leave {name} out of the next backups?";
        ExcludeRuleIntro = $"Backspace adds this rule to {backup.Name}'s own filter rules:";
        var after = FolderExclusion.Add(backup.FiltersText, ExcludeRule);
        ExcludeWarning = FolderExclusion.KeptBy(after, path) is { } keeper
            ? $"This rule would change nothing: an exception further down keeps the folder in ({keeper}). To leave it out, change that exception in the backup's filters."
            : "";
        ExcludeError = "";
        ExcludeOnDisk = "Counting what it holds now…";
        IsConfirmingExclude = true;
        CancelMeasure();
        var cts = _measureCts = new CancellationTokenSource();
        var measured = await Task.Run(() => FolderExclusion.Measure(path, TimeSpan.FromSeconds(3), cts.Token));
        if (cts.IsCancellationRequested || !ReferenceEquals(cts, _measureCts)) return;
        var c = CultureInfo.InvariantCulture;
        ExcludeOnDisk = measured is { } m
            ? $"It holds about {m.Files.ToString("N0", c)} {(m.Files == 1 ? "file" : "files")}, {BackupLiveProgress.Bytes(m.Bytes)} on disk now."
            : "";
    }

    [RelayCommand]
    private void CancelExclude()
    {
        CancelMeasure();
        IsConfirmingExclude = false;
    }

    /// <summary>Adds the rule to the backup's custom block and saves the
    /// settings, which the background service reads too.</summary>
    [RelayCommand]
    private void ConfirmExclude()
    {
        if (!CanConfirmExclude || ExcludePath is not { } path || ExcludeRule.Length == 0) return;
        var rule = ExcludeRule;
        string? before = null, after = null;
        try
        {
            ServiceLocator.Config.Update(cfg =>
            {
                if (cfg.Backups.FirstOrDefault(b => b.Id == _backup.Id) is not { } b) return;
                before = b.FiltersText ?? "";
                after = FolderExclusion.Add(before, rule);
                b.FiltersText = after;
            });
        }
        catch (Exception ex)
        {
            ExcludeError = "The settings couldn't be saved: " + ex.Message;
            return;
        }
        if (before is null || after is null) { ExcludeError = "This backup no longer exists."; return; }
        if (after != before) _excluded[path] = (rule, before, after);
        RefreshExclusion();
    }

    /// <summary>Takes out exactly the rule this window added for the opened folder.</summary>
    [RelayCommand]
    private void UndoExclude()
    {
        if (ExcludePath is not { } path || !_excluded.TryGetValue(path, out var added)) return;
        try
        {
            ServiceLocator.Config.Update(cfg =>
            {
                if (cfg.Backups.FirstOrDefault(b => b.Id == _backup.Id) is not { } b) return;
                // Unchanged since: put back the text as it was; otherwise take
                // out just the rule, keeping later edits.
                b.FiltersText = b.FiltersText == added.After ? added.Before : FolderExclusion.Remove(b.FiltersText, added.Rule);
            });
        }
        catch (Exception ex)
        {
            ExcludeError = "The settings couldn't be saved: " + ex.Message;
            return;
        }
        _excluded.Remove(path);
        RefreshExclusion();
    }

    private void CancelMeasure()
    {
        if (_measureCts is null) return;
        try { _measureCts.Cancel(); } catch (ObjectDisposedException) { }
        _measureCts = null;
    }

    public void Dispose()
    {
        CancelMeasure();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
