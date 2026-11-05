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

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }
}
