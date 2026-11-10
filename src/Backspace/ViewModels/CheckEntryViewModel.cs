using System;
using Backspace.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backspace.ViewModels;

/// <summary>One check in the backup editor's Checks list (U179): its own
/// frequency, time and conditions, and the settings of its kind.</summary>
public sealed partial class CheckEntryViewModel : ObservableObject
{
    private readonly string _id;
    private readonly Action<CheckEntryViewModel> _remove;
    private readonly Action _changed;
    private readonly int _weeklyDays;

    public CheckKind Kind { get; }

    [ObservableProperty] private MaintenanceCadence _cadence;
    [ObservableProperty] private TimeSpan _timeOfDay;
    [ObservableProperty] private bool _skipOnBattery;
    [ObservableProperty] private bool _requireNetwork;
    [ObservableProperty] private bool _catchUpMissed;
    /// <summary>Data sample and test restore: the most to read or rebuild.</summary>
    [ObservableProperty] private decimal _maximumGiB;
    [ObservableProperty] private decimal _samplePercent;
    [ObservableProperty] private bool _compareOriginals;

    public CheckEntryViewModel(BackupCheck check, BackupSchedule main, Action<CheckEntryViewModel> remove, Action changed)
    {
        _id = check.Id;
        _remove = remove;
        _changed = changed;
        Kind = check.Kind;
        var schedule = check.Schedule;
        _weeklyDays = schedule is { Frequency: ScheduleFrequency.Weekly, WeeklyDaysBitmask: not 0 and not 0b0111_1111 } ? schedule.WeeklyDaysBitmask : 0b0000_0001;
        _cadence = schedule?.Frequency switch
        {
            null => MaintenanceCadence.AfterEachBackup,
            ScheduleFrequency.Weekly => schedule.WeeklyDaysBitmask == 0b0111_1111 ? MaintenanceCadence.Daily : MaintenanceCadence.Weekly,
            ScheduleFrequency.Monthly => MaintenanceCadence.Monthly,
            ScheduleFrequency.Yearly => MaintenanceCadence.Yearly,
            _ => MaintenanceCadence.Daily,
        };
        _timeOfDay = schedule?.TimeOfDay ?? BackupMaintenanceDefaults.OffHours;
        var conditions = schedule ?? main;
        _skipOnBattery = conditions.SkipOnBattery;
        _requireNetwork = conditions.RequireNetwork;
        _catchUpMissed = conditions.CatchUpMissedRuns;
        _stopOnBattery = conditions.StopOnBattery;
        _maximumGiB = check.MaximumBytes / (decimal)BackupCheck.OneGiB;
        _samplePercent = (decimal)check.SamplePercent;
        _compareOriginals = check.CompareOriginals;
    }

    private readonly bool _stopOnBattery;

    public static MaintenanceCadence[] Cadences { get; } =
        [MaintenanceCadence.AfterEachBackup, MaintenanceCadence.Daily, MaintenanceCadence.Weekly, MaintenanceCadence.Monthly, MaintenanceCadence.Yearly];

    public bool ShowsTime => Cadence != MaintenanceCadence.AfterEachBackup;
    public bool IsDataSample => Kind == CheckKind.DataSample;
    public bool IsTestRestore => Kind == CheckKind.TestRestore;
    /// <summary>No size settings: these read all of something.</summary>
    public bool IsStructureOrFull => Kind is CheckKind.Structure or CheckKind.FullDataRead;

    /// <summary>The heading: "Data sample (1 GiB)".</summary>
    public string Title => ToModel(validate: false).Title;

    /// <summary>"Weekly on Sunday at 03:00" or "After each backup".</summary>
    public string When => ToModel(validate: false).Schedule is { } s ? s.DescribePeriod() : "After each backup that reaches the destination";

    /// <summary>What the check does and proves, for someone who isn't technical.</summary>
    public string Explanation => Explain(Kind);

    public static string Explain(CheckKind kind) => kind switch
    {
        CheckKind.Structure =>
            "Checks that the backup's records are readable and complete, and that every piece of data they point to is still there. "
            + "It reads no file contents, so it is quick.",
        CheckKind.DataSample =>
            "Downloads a random part of the stored data, from any backup old or new, and checks each piece against its fingerprint. "
            + "This proves the storage hasn't lost or damaged your data. It includes the structure check. It does not rebuild any file.",
        CheckKind.TestRestore =>
            "Rebuilds real files from the latest backup, the way a restore does: following the folder list, unlocking them with your password, "
            + "and writing them to disk. This proves the whole way back to usable files works. Each time it takes files never tested first, "
            + "then those tested longest ago. With “compare with today's originals” it also catches files that were read wrongly when they were backed up.",
        _ =>
            "Downloads all the stored data and checks every piece against its fingerprint. The most thorough check of the storage, and the slowest: "
            + "on a large cloud backup it can take many hours and download everything.",
    };

    public string LimitHelp => Kind == CheckKind.DataSample
        ? "Reads 1% of the stored data, up to this amount. Whole pieces are read, so a little more can be downloaded. Cloud downloads may cost money."
        : "Stops at the smaller of the two. Whole files only: a file larger than the limit is passed over. 0 GiB means no limit.";

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnCadenceChanged(MaintenanceCadence value) { OnPropertyChanged(nameof(ShowsTime)); Changed(); }
    partial void OnTimeOfDayChanged(TimeSpan value) => Changed();
    partial void OnMaximumGiBChanged(decimal value) => Changed();
    partial void OnSamplePercentChanged(decimal value) => Changed();
    partial void OnCompareOriginalsChanged(bool value) => Changed();
    partial void OnSkipOnBatteryChanged(bool value) => Changed();
    partial void OnRequireNetworkChanged(bool value) => Changed();
    partial void OnCatchUpMissedChanged(bool value) => Changed();

    private void Changed()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(When));
        _changed();
    }

    /// <summary>The entry as saved. Throws when a value can't be used.</summary>
    public BackupCheck ToModel(bool validate = true)
    {
        if (validate)
        {
            if (Kind == CheckKind.DataSample && MaximumGiB <= 0)
                throw new ArgumentException("Give the data sample an amount above 0 GiB.");
            if (Kind == CheckKind.TestRestore && (SamplePercent <= 0 || SamplePercent > 100 || MaximumGiB < 0))
                throw new ArgumentException("Give the test restore a share above 0% and at most 100%, and a limit of 0 GiB or more.");
        }
        return new BackupCheck
        {
            Id = _id,
            Kind = Kind,
            MaximumBytes = (long)(Math.Max(0, MaximumGiB) * BackupCheck.OneGiB),
            SamplePercent = (double)SamplePercent,
            CompareOriginals = Kind == CheckKind.TestRestore && CompareOriginals,
            Schedule = Cadence == MaintenanceCadence.AfterEachBackup ? null : new BackupSchedule
            {
                Frequency = Cadence switch
                {
                    MaintenanceCadence.Weekly => ScheduleFrequency.Weekly,
                    MaintenanceCadence.Monthly => ScheduleFrequency.Monthly,
                    MaintenanceCadence.Yearly => ScheduleFrequency.Yearly,
                    _ => ScheduleFrequency.Daily,
                },
                TimeOfDay = TimeOfDay,
                HourlyInterval = 24,
                WeeklyDaysBitmask = Cadence == MaintenanceCadence.Weekly ? _weeklyDays : 0b0111_1111,
                SkipOnBattery = SkipOnBattery,
                StopOnBattery = _stopOnBattery,
                RequireNetwork = RequireNetwork,
                CatchUpMissedRuns = CatchUpMissed,
            },
        };
    }
}
