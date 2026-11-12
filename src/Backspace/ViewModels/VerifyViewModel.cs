using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Backspace.ViewModels;

/// <summary>The Verify window's answer: checks to run, then a test restore once.</summary>
public sealed record VerifyChoices(IReadOnlyList<BackupCheck> Checks);

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
                "Is the backup complete? Fast: reads its records and confirms all their data is there. Reads no file contents.", selected: false));
        if (!kinds.Contains(CheckKind.DataSample))
            Other.Add(Choice(new BackupCheck { Kind = CheckKind.DataSample, MaximumBytes = BackupCheck.OneGiB }, "Data check (1 GiB sample)",
                "Is the stored data intact? Checks the structure, then downloads about 1 GiB of the stored data and checks it.", selected: false));
        _otherRestore = Choice(new BackupCheck { Kind = CheckKind.TestRestore }, "Restore check",
            "Can your files come back? Restores some files into a temporary folder and checks them. Choose how many below.", selected: false, RestoreOptions);
        Other.Add(_otherRestore);
        if (!kinds.Contains(CheckKind.FullDataRead))
            Other.Add(Choice(new BackupCheck { Kind = CheckKind.FullDataRead }, "Data check (all data)",
                "Checks the structure, then downloads all the stored data and checks every piece. The most thorough, and the slowest.", selected: false));
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
        var other = Other.Where(c => c.IsSelected && c != _otherRestore)
            .Select(c => { var once = c.Check.Clone(); once.Id = Guid.NewGuid().ToString("N"); once.Schedule = null; return once; });
        var chosen = own.Concat(BackupCheck.InRunOrder(other.ToList())).ToList();
        // The test restore once runs last, like the backup's own checks: it
        // takes the destination in turn, shows on the card with Stop, and its
        // result goes to Activity (U203, U205).
        if (OnceRestore() is { } options)
            chosen.Add(new BackupCheck { Id = Guid.NewGuid().ToString("N"), Kind = CheckKind.TestRestore, Schedule = null,
                SamplePercent = options.SamplePercent, MaximumBytes = options.MaximumSampleBytes, CompareOriginals = options.CompareOriginals,
                OnceOptions = options });
        return chosen;
    }

    /// <summary>The once-only test restore's choices, or null when it isn't ticked.</summary>
    public TestRestoreOptions? OnceRestore() => _otherRestore?.IsSelected == true ? RestoreOptions.ToOptions() : null;

    /// <summary>What the window hands back.</summary>
    public VerifyChoices Result() => new(Selected());

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
