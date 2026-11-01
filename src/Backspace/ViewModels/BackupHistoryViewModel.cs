using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Backspace.ViewModels;

public sealed partial class BackupHistoryViewModel : ObservableObject
{
    private readonly Backup _backup;
    private IReadOnlyList<RunRecord> _runs;
    private Dictionary<string, RunRecord> _byId;
    public string Title => _backup.Name + " · history";
    public string Schedule => _backup.Enabled ? "Backup schedule: " + _backup.Schedule.DescribeNextRun(DateTime.Now) : "Automatic backups are off.";
    public string[] Filters { get; } = { "Backup runs", "Checks", "All activity" };
    [ObservableProperty] private string _selectedFilter;
    public ObservableCollection<BackupHistoryRow> Rows { get; } = new();
    public bool IsEmpty => Rows.Count == 0;
    public BackupHistoryViewModel(Backup backup, IReadOnlyList<RunRecord> runs, bool checks)
    { _backup = backup; _runs = runs; _byId = runs.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First()); _selectedFilter = checks ? "Checks" : "Backup runs"; Refresh(); }
    partial void OnSelectedFilterChanged(string value) => Refresh();
    public void UpdateRuns(IReadOnlyList<RunRecord> runs)
    {
        _runs = runs;
        _byId = runs.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        Refresh();
    }
    private void Refresh()
    {
        Rows.Clear();
        if (SelectedFilter == "Checks")
            foreach (var entry in BackupCheckHistory.Entries(_backup, _runs))
                Rows.Add(new(entry.RunId, _backup.Id, entry.When, entry.Status, entry.Scope, entry.Summary,
                    HasLog(_byId.GetValueOrDefault(entry.RunId))));
        else
            foreach (var run in _runs.Where(r => r.BackupId == _backup.Id && (SelectedFilter == "All activity" || r.Kind == RunKind.Backup)).OrderByDescending(r => r.StartedUtc))
                Rows.Add(new(run.Id, _backup.Id, run.StartedUtc, run.Status,
                    run.Kind switch { RunKind.TestRestore => run.TriggeredAutomatically ? "Automatic test restore" : "Test restore", RunKind.Check => "Repository check", _ => run.Kind.ToString() }, run.Summary,
                    HasLog(run)));
        OnPropertyChanged(nameof(IsEmpty));
    }
    private static bool HasLog(RunRecord? run) => run is not null
        && (System.IO.File.Exists(run.LogPath) || run.Cells.Any(c => System.IO.File.Exists(c.LogPath)));
}

public sealed record BackupHistoryRow(string RunId, string BackupId, DateTime When, BackupRunStatus Status, string Kind, string Summary, bool HasLog)
{
    public string Timestamp => When.ToLocalTime().ToString("ddd d MMM yyyy · HH:mm:ss") + " · " + HumanFormat.Ago(When);
    public string Result => Status switch { BackupRunStatus.Success => "Passed", BackupRunStatus.Failed => "Failed", BackupRunStatus.Skipped => "Skipped / cancelled", BackupRunStatus.Warning => "Needs attention", _ => Status.ToString() };
    public bool Failed => Status == BackupRunStatus.Failed;
    public bool Passed => Status == BackupRunStatus.Success;
}
