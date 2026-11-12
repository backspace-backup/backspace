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

    /// <summary>A data check switches between a sample and all data (U221).</summary>
    public CheckKind Kind { get; private set; }

    [ObservableProperty] private MaintenanceCadence _cadence;
    [ObservableProperty] private TimeSpan _timeOfDay;
    /// <summary>Its own conditions instead of the backup's (U226).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FollowsBackup))] private bool _ownConditions;
    /// <summary>"Run only while on AC power", with its own conditions.</summary>
    [ObservableProperty] private bool _skipOnBattery;
    /// <summary>"Run only while on an unmetered connection", with its own conditions.</summary>
    [ObservableProperty] private bool _unmeteredOnly;

    /// <summary>"Same conditions as the backup" (U226).</summary>
    public bool FollowsBackup { get => !OwnConditions; set => OwnConditions = !value; }
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
        // Its own conditions, or a copy of the backup's to start from (U226).
        _ownConditions = schedule?.OwnConditions == true;
        _skipOnBattery = _ownConditions ? schedule!.SkipOnBattery : main.SkipOnBattery;
        _unmeteredOnly = _ownConditions ? schedule!.UnmeteredOnly : main.UnmeteredOnly;
        _maximumGiB = check.MaximumBytes / (decimal)BackupCheck.OneGiB;
        _samplePercent = (decimal)check.SamplePercent;
        _compareOriginals = check.CompareOriginals;
    }


    public static MaintenanceCadence[] Cadences { get; } =
        [MaintenanceCadence.AfterEachBackup, MaintenanceCadence.Daily, MaintenanceCadence.Weekly, MaintenanceCadence.Monthly, MaintenanceCadence.Yearly];

    public bool ShowsTime => Cadence != MaintenanceCadence.AfterEachBackup;
    public bool IsDataSample => Kind == CheckKind.DataSample;
    /// <summary>A data check, of a sample or of all data (U221).</summary>
    public bool IsDataCheck => Kind is CheckKind.DataSample or CheckKind.FullDataRead;
    /// <summary>The engine can read all data (restic always can; a test engine may not).</summary>
    public bool AllDataAvailable => !Services.ServiceLocator.Initialized || Services.ServiceLocator.Engine.HonoursDataCheckLimit;
    /// <summary>"All data": the data check reads everything instead of a sample.</summary>
    public bool AllData
    {
        get => Kind == CheckKind.FullDataRead;
        set
        {
            if (!IsDataCheck || value == AllData) return;
            Kind = value ? CheckKind.FullDataRead : CheckKind.DataSample;
            if (!value && MaximumGiB <= 0) MaximumGiB = 1;
            foreach (var name in new[] { nameof(Kind), nameof(AllData), nameof(IsDataSample), nameof(IsStructureOrFull), nameof(Explanation), nameof(LimitHelp) })
                OnPropertyChanged(name);
            Changed();
        }
    }
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
            "Is the backup complete? Checks that its records are readable and complete, and that every piece of data they point to is still there. "
            + "It reads no file contents, so it is quick.",
        CheckKind.DataSample =>
            "Is the stored data intact? Downloads a random sample of the stored data, from any backup old or new, and checks each piece against its fingerprint. "
            + "It checks the structure first, so a separate structure check isn't needed. It doesn't rebuild any file: the restore check does.",
        CheckKind.TestRestore =>
            "Can your files come back? Rebuilds real files from the latest backup into a temporary folder, the way a restore does: following the folder list, unlocking them with your password, "
            + "and writing them to disk. This proves the whole way back to usable files works. Each time it takes files never tested first, "
            + "then those tested longest ago. With “compare with today's originals” it also catches files that were read wrongly when they were backed up.",
        _ =>
            "Is the stored data intact? Downloads all the stored data and checks every piece against its fingerprint, after the structure. The most thorough check of the storage, and the slowest: "
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
    partial void OnUnmeteredOnlyChanged(bool value) => Changed();
    partial void OnOwnConditionsChanged(bool value) => Changed();

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
                throw new ArgumentException("Give the data check a sample above 0 GiB, or tick All data.");
            if (Kind == CheckKind.TestRestore && (SamplePercent <= 0 || SamplePercent > 100 || MaximumGiB < 0))
                throw new ArgumentException("Give the restore check a share above 0% and at most 100%, and a limit of 0 GiB or more.");
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
                OwnConditions = OwnConditions,
                SkipOnBattery = SkipOnBattery,
                StopOnBattery = SkipOnBattery,
                UnmeteredOnly = UnmeteredOnly,
                RequireNetwork = true,
                CatchUpMissedRuns = true,
            },
        };
    }
}
