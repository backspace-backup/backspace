using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.WebDav;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backspace.ViewModels;

/// <summary>
/// Mount's first step (U162): which revisions should the drive show? The
/// latest of each source is ticked; the person may tick more, or all. Only
/// the ticked revisions' trees are read, all before the drive appears. The
/// owner, 7 November 2026: "it is unlikely people would need to check
/// multiple revisions in the same mount." Nothing is remembered beyond the
/// dialog.
/// </summary>
public sealed partial class MountRevisionPickerViewModel : ObservableObject
{
    private readonly Backup _backup;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<RevisionSummary>>> _listRevisions;
    private readonly Func<DateTime> _now;

    public MountRevisionPickerViewModel(Backup backup,
        Func<string, CancellationToken, Task<IReadOnlyList<RevisionSummary>>> listRevisions,
        Func<DateTime>? now = null)
    {
        _backup = backup;
        _listRevisions = listRevisions;
        _now = now ?? (() => DateTime.Now);
    }

    public string Title => $"Mount “{_backup.Name}”";

    public ObservableCollection<MountRevisionGroup> Groups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMount))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    private bool _isLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMount))]
    [NotifyPropertyChangedFor(nameof(ShowList))]
    private string? _error;

    public bool ShowList => !IsLoading && Error is null;
    public bool HasSeveralSources => Groups.Count > 1;

    private IEnumerable<MountRevisionRow> Rows => Groups.SelectMany(g => g.Rows);
    public int SelectedCount => Rows.Count(r => r.IsSelected);
    public int RevisionCount => Rows.Count();
    public bool CanMount => !IsLoading && Error is null && SelectedCount > 0;
    public bool AllSelected => RevisionCount > 0 && SelectedCount == RevisionCount;
    public bool ShowSelectAll => RevisionCount > Groups.Count;

    public string MountLabel => SelectedCount <= Groups.Count(g => g.Rows.Any(r => r.IsSelected))
        ? "Mount" : $"Mount {SelectedCount} revisions";
    public string SelectAllLabel => AllSelected ? "Latest only" : $"Select all {RevisionCount}";

    /// <summary>Said under the list: what the choice costs.</summary>
    public string Hint => SelectedCount == 0
        ? "Tick at least one revision to mount."
        : SeveralPerSource
            ? $"Backspace reads the folders of each of the {SelectedCount} revisions before the drive opens, one after the other. " +
              "For a large backup that takes about as long per revision as mounting one. The drive shows one folder per revision, named by its date and time."
            : "Backspace reads this revision's folders before the drive opens; after that every folder opens at once. Only the files you open are downloaded.";

    private bool SeveralPerSource => Groups.Any(g => g.Rows.Count(r => r.IsSelected) > 1);

    /// <summary>Lists every source's revisions, newest first, and ticks the
    /// latest of each.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        Error = null;
        Groups.Clear();
        try
        {
            var folders = BackupMountTreeProvider.BuildSourceFolderMap(_backup.SourcePaths)
                .ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
            var lists = await Task.WhenAll(_backup.SourcePaths.Select(path => _listRevisions(path, ct))).ConfigureAwait(true);
            for (var i = 0; i < lists.Length; i++)
            {
                if (lists[i].Count == 0) continue;
                var path = _backup.SourcePaths[i];
                var rows = lists[i].OrderByDescending(r => r.CreatedUtc)
                    .Select((r, index) => new MountRevisionRow(r, index == 0, _now(), Changed) { IsSelected = index == 0 })
                    .ToList();
                Groups.Add(new MountRevisionGroup(folders.TryGetValue(path, out var folder) ? folder : path, path, rows,
                    ShowHeading: _backup.SourcePaths.Count > 1));
            }
            if (Groups.Count == 0) Error = "No snapshots yet. Run a backup first, then come back to mount it.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppLogger.For<MountRevisionPickerViewModel>().Warning(ex, "Listing revisions to mount failed for {Backup}", _backup.Name);
            Error = "Couldn't read this backup's revisions. Check the destination is reachable and try again. " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            Changed();
        }
    }

    /// <summary>Ticks every revision, or back to the latest of each source.</summary>
    [RelayCommand]
    private void ToggleAll()
    {
        var all = !AllSelected;
        foreach (var group in Groups)
            for (var i = 0; i < group.Rows.Count; i++)
                group.Rows[i].IsSelected = all || i == 0;
    }

    /// <summary>The ticked revisions, or null when none are.</summary>
    public MountSelection? ToSelection()
    {
        if (!CanMount) return null;
        return new MountSelection(Groups
            .Select(g => new MountSelection.Source(g.SourcePath, g.Rows.Where(r => r.IsSelected).Select(r => r.Revision).ToList()))
            .Where(s => s.Revisions.Count > 0).ToList());
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(RevisionCount));
        OnPropertyChanged(nameof(CanMount));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(ShowSelectAll));
        OnPropertyChanged(nameof(MountLabel));
        OnPropertyChanged(nameof(SelectAllLabel));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(HasSeveralSources));
    }
}

/// <summary>One source's revisions; the heading shows only with several sources.</summary>
public sealed record MountRevisionGroup(string Folder, string SourcePath, IReadOnlyList<MountRevisionRow> Rows, bool ShowHeading);

/// <summary>One revision in the mount picker: "Today 14:30", "Latest",
/// "612,345 files · 190 GB" when the snapshot recorded its totals.</summary>
public sealed partial class MountRevisionRow : ObservableObject
{
    private readonly Action _changed;

    public MountRevisionRow(RevisionSummary revision, bool isLatest, DateTime now, Action changed)
    {
        Revision = revision;
        IsLatest = isLatest;
        _changed = changed;
        var local = revision.CreatedUtc.ToLocalTime();
        var en = CultureInfo.InvariantCulture;
        When = (local.Date == now.Date ? "Today"
            : local.Date == now.Date.AddDays(-1) ? "Yesterday"
            : local.ToString(local.Year == now.Year ? "ddd d MMM" : "ddd d MMM yyyy", en))
            + ", " + local.ToString("HH:mm", en);
        Details = revision.FileCount > 0 || revision.TotalBytes > 0
            ? string.Join(" · ", new[]
            {
                revision.FileCount > 0 ? $"{revision.FileCount.ToString("N0", en)} files" : null,
                revision.TotalBytes > 0 ? BackupLiveProgress.Bytes(revision.TotalBytes) : null,
            }.Where(s => s is not null))
            : "";
    }

    public RevisionSummary Revision { get; }
    public bool IsLatest { get; }
    public string When { get; }
    public string Details { get; }
    public bool HasDetails => Details.Length > 0;

    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => _changed();
}
