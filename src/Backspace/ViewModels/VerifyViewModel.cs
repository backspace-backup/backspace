using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Backspace.ViewModels;

/// <summary>One line of the Verify window: a check to tick.</summary>
public sealed partial class VerifyChoice : ObservableObject
{
    public required BackupCheck Check { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    /// <summary>The once-only test restore's sample size, shown under it while ticked.</summary>
    public RestoreCheckOptionsViewModel? Options { get; init; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsOptions))] private bool _isSelected;
    public bool ShowsOptions => Options is not null && IsSelected;
}

/// <summary>
/// The card's Verify window (U213, owner, 12 November 2026: "replace it with a
/// Check health button where it offers to trigger one or multiple health
/// checks manually ... still as intuitive as possible"). This backup's own
/// checks, ticked, each with its latest result; then the other kinds of check,
/// to run once without being added to the backup. They run one after another.
/// </summary>
public sealed partial class VerifyViewModel : ObservableObject
{
    public string Heading { get; }
    public ObservableCollection<VerifyChoice> Own { get; } = new();
    public ObservableCollection<VerifyChoice> Other { get; } = new();
    public bool HasOwnChecks => Own.Count > 0;
    /// <summary>The once-only test restore's sample size.</summary>
    public RestoreCheckOptionsViewModel RestoreOptions { get; }
    private readonly VerifyChoice? _otherRestore;

    public VerifyViewModel(Backup backup, IReadOnlyList<BackupCheckEntry> entries, DateTime nowUtc)
    {
        Heading = $"Verify “{backup.Name}”";
        foreach (var check in BackupCheck.InRunOrder(backup.Checks))
            Own.Add(Choice(check, check.Title, Capitalize(BackupCheckHistory.LatestOf(backup, check, entries, nowUtc)), selected: true));
        var restore = backup.Checks.FirstOrDefault(c => c.Kind == CheckKind.TestRestore);
        RestoreOptions = new RestoreCheckOptionsViewModel(restore?.RestoreOptions() ?? new TestRestoreOptions(false, 1, BackupCheck.OneGiB, false));
        var kinds = backup.Checks.Select(c => c.Kind).ToHashSet();
        if (!kinds.Contains(CheckKind.Structure))
            Other.Add(Choice(new BackupCheck { Kind = CheckKind.Structure }, "Structure check",
                "Fast: confirms the backup's index and snapshots are complete. Reads no stored data.", selected: false));
        if (!kinds.Contains(CheckKind.DataSample))
            Other.Add(Choice(new BackupCheck { Kind = CheckKind.DataSample, MaximumBytes = BackupCheck.OneGiB }, "Structure check + data sample (1 GiB)",
                "Also downloads about 1 GiB of the stored data and checks it is intact.", selected: false));
        _otherRestore = Choice(new BackupCheck { Kind = CheckKind.TestRestore }, "Test restore",
            "Restores a sample of files into a temporary folder and checks them. Choose its size below.", selected: false, RestoreOptions);
        Other.Add(_otherRestore);
        if (!kinds.Contains(CheckKind.FullDataRead))
            Other.Add(Choice(new BackupCheck { Kind = CheckKind.FullDataRead }, "Full data read",
                "Downloads all the stored data and checks every piece. The most thorough, and the slowest.", selected: false));
        // Nothing of its own: the structure check is ticked, the fast one.
        if (Own.Count == 0 && Other.FirstOrDefault() is { } first) first.IsSelected = true;
        RestoreOptions.PropertyChanged += (_, _) => Changed();
    }

    private VerifyChoice Choice(BackupCheck check, string title, string detail, bool selected, RestoreCheckOptionsViewModel? options = null)
    {
        var choice = new VerifyChoice { Check = check, Title = title, Detail = detail, IsSelected = selected, Options = options };
        choice.PropertyChanged += (_, _) => Changed();
        return choice;
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(RunLabel));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(ShowsRestoreOptions));
    }

    public bool ShowsRestoreOptions => _otherRestore?.IsSelected == true;
    private int Count => Own.Count(c => c.IsSelected) + Other.Count(c => c.IsSelected);
    public bool CanRun => Count > 0;
    public string RunLabel => Count switch { 0 => "Run checks", 1 => "Run 1 check", var n => $"Run {n} checks" };

    /// <summary>The ticked checks, in run order: this backup's first, then the
    /// once-only ones, cheapest first. A once-only check has an id of its own,
    /// so it is not taken for one of the backup's.</summary>
    public List<BackupCheck> Selected()
    {
        var own = Own.Where(c => c.IsSelected).Select(c => c.Check);
        var other = Other.Where(c => c.IsSelected).Select(c =>
        {
            if (c != _otherRestore) return c.Check.Clone();
            var options = RestoreOptions.ToOptions();
            return new BackupCheck
            {
                Kind = CheckKind.TestRestore,
                SamplePercent = options.Full ? 100 : options.SamplePercent,
                MaximumBytes = options.Full ? 0 : options.MaximumSampleBytes,
                CompareOriginals = options.CompareOriginals,
            };
        }).Select(c => { c.Id = Guid.NewGuid().ToString("N"); c.Schedule = null; return c; });
        return own.Concat(BackupCheck.InRunOrder(other.ToList())).ToList();
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
