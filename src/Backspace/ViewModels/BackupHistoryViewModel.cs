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
                    HasLog(run)) { Added = Added(run), CanShowChanges = run.Kind == RunKind.Backup && run.Status is BackupRunStatus.Success or BackupRunStatus.Warning });
        OnPropertyChanged(nameof(IsEmpty));
    }
    /// <summary>The backup and the run a row stands for, to show what it added (U143).</summary>
    internal (Backup Backup, RunRecord? Run) RunFor(string runId) => (_backup, _byId.GetValueOrDefault(runId));

    /// <summary>"476 new · 157 changed · 1.6 GB added" for a backup (U142):
    /// recorded with the run, or read from its own log's recap for runs saved
    /// before the figures were recorded.</summary>
    internal static string Added(RunRecord run)
    {
        if (run.Kind != RunKind.Backup || run.Status is not (BackupRunStatus.Success or BackupRunStatus.Warning)) return "";
        var stats = run.FilesNew + run.FilesModified > 0 || run.BytesUploaded > 0
            ? (run.FilesNew, run.FilesModified, run.BytesUploaded)
            : BackupLiveProgress.StatsFromLog(run.LogPath) ?? run.Cells.Select(c => BackupLiveProgress.StatsFromLog(c.LogPath)).FirstOrDefault(s => s is not null);
        if (stats is not { } s) return "";
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return $"{s.Item1.ToString("N0", c)} new · {s.Item2.ToString("N0", c)} changed · {BackupLiveProgress.Bytes(s.Item3)} added";
    }

    private static bool HasLog(RunRecord? run) => run is not null
        && (run.LogKeptByService || System.IO.File.Exists(run.LogPath) || run.Cells.Any(c => System.IO.File.Exists(c.LogPath))
            || string.IsNullOrEmpty(run.LogPath) && ServiceLogFiles.LogPaths(run).Count > 0);
}

public sealed record BackupHistoryRow(string RunId, string BackupId, DateTime When, BackupRunStatus Status, string Kind, string Summary, bool HasLog)
{
    /// <summary>What a backup added (U142), or empty.</summary>
    public string Added { get; init; } = "";
    /// <summary>A backup that made a snapshot: its added files can be shown (U143).</summary>
    public bool CanShowChanges { get; init; }
    public bool HasAdded => Added.Length > 0;
    public string Timestamp => When.ToLocalTime().ToString("ddd d MMM yyyy · HH:mm:ss") + " · " + HumanFormat.Ago(When);
    public string Result => Status switch { BackupRunStatus.Success => "Passed", BackupRunStatus.Failed => "Failed", BackupRunStatus.Skipped => "Skipped / cancelled", BackupRunStatus.Warning => "Needs attention", _ => Status.ToString() };
    public bool Failed => Status == BackupRunStatus.Failed;
    public bool Passed => Status == BackupRunStatus.Success;
}
