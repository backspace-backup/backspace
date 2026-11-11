using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Xunit;

namespace Backspace.Tests.Models;

/// <summary>U179 (10 November 2026): checks are a list, one entry per check.
/// Settings saved before then convert into equivalent entries, in the app and
/// in the background service's strict reader alike.</summary>
public sealed class BackupChecksTests
{
    // The app's own settings options (ConfigStore).
    private static readonly JsonSerializerOptions App = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // The owner's settings as saved on 10 November 2026 (check fields only).
    private const string Monthly2100 = """{"frequency":"Monthly","timeOfDay":"21:00:00","weeklyDaysBitmask":127,"hourlyInterval":24,"customExpression":"","skipOnBattery":true,"stopOnBattery":true,"requireNetwork":true,"catchUpMissedRuns":true,"executionTimeLimit":"3.00:00:00"}""";
    private const string Options = """{"full":false,"samplePercent":1,"maximumSampleBytes":1073741824,"compareOriginals":false,"label":"Sample test restore (1%)"}""";

    private static string Legacy(string name, string checkSchedule, bool readData, bool restoreFiles) =>
        $$"""{"name":"{{name}}","checkSchedule":{{checkSchedule}},"pruneSchedule":null,"checkReadDataSample":{{(readData ? "true" : "false")}},"checkRestoreFiles":{{(restoreFiles ? "true" : "false")}},"restoreCheckOptions":{{Options}}}""";

    public static TheoryData<string> Readers => new() { "app", "service" };

    private static Backup Read(string json, string reader) => JsonSerializer.Deserialize<Backup>(json, reader == "app" ? App : ServiceRunAuthorization.Json)!;

    [Theory]
    [MemberData(nameof(Readers))]
    public void Local_c_and_local_dropbox_become_a_monthly_structure_check_and_test_restore(string reader)
    {
        foreach (var name in new[] { "local_c", "local_dropbox" })
        {
            var backup = Read(Legacy(name, Monthly2100, readData: false, restoreFiles: true), reader);
            Assert.Equal(new[] { CheckKind.Structure, CheckKind.TestRestore }, backup.Checks.Select(c => c.Kind));
            Assert.All(backup.Checks, c =>
            {
                Assert.Equal(ScheduleFrequency.Monthly, c.Schedule!.Frequency);
                Assert.Equal(TimeSpan.FromHours(21), c.Schedule.TimeOfDay);
                Assert.True(c.Schedule.SkipOnBattery && c.Schedule.RequireNetwork && c.Schedule.CatchUpMissedRuns);
            });
            var restore = backup.Checks[1];
            Assert.Equal(1, restore.SamplePercent);
            Assert.Equal(1073741824, restore.MaximumBytes);
            Assert.False(restore.CompareOriginals);
            Assert.False(backup.CheckReadDataSample);
        }
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void Local_d_becomes_a_monthly_data_sample_and_test_restore(string reader)
    {
        // restic's data sample also checks the structure: the same commands as before.
        var backup = Read(Legacy("local_d", Monthly2100, readData: true, restoreFiles: true), reader);
        Assert.Equal(new[] { CheckKind.DataSample, CheckKind.TestRestore }, backup.Checks.Select(c => c.Kind));
        Assert.Equal(BackupCheck.OneGiB, backup.Checks[0].MaximumBytes);
        Assert.All(backup.Checks, c => Assert.Equal(TimeSpan.FromHours(21), c.Schedule!.TimeOfDay));
        Assert.Equal("Data sample (1 GiB)", backup.Checks[0].Title);
        Assert.Equal("Sample test restore (1%, at most 1 GiB)", backup.Checks[1].Title);
        // The saved backup no longer asks the engine for a data read itself.
        Assert.False(backup.CheckReadDataSample);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void A_structure_only_monthly_check_and_an_after_each_backup_check_convert_too(string reader)
    {
        var sms = Read(Legacy("smsbackuprestore", Monthly2100.Replace("21:00:00", "03:00:00"), readData: false, restoreFiles: false), reader);
        var only = Assert.Single(sms.Checks);
        Assert.Equal(CheckKind.Structure, only.Kind);
        Assert.Equal(TimeSpan.FromHours(3), only.Schedule!.TimeOfDay);
        // No check schedule saved at all meant a check after each backup.
        var inline = Read("""{"name":"old"}""", reader);
        Assert.Null(Assert.Single(inline.Checks).Schedule);
    }

    [Fact]
    public void Converted_settings_save_without_the_old_fields_and_load_unchanged()
    {
        var backup = Read(Legacy("local_d", Monthly2100, readData: true, restoreFiles: true), "app");
        var saved = JsonSerializer.Serialize(backup, App);
        foreach (var old in new[] { "checkSchedule", "checkReadDataSample", "checkRestoreFiles", "restoreCheckOptions", "dataCheckLimitBytes" })
            Assert.DoesNotContain("\"" + old + "\"", saved);
        Assert.Contains("\"checks\"", saved);
        var again = Read(saved, "service");
        Assert.Equal(backup.Checks.Select(c => (c.Id, c.Kind, c.Schedule!.Frequency, c.MaximumBytes)), again.Checks.Select(c => (c.Id, c.Kind, c.Schedule!.Frequency, c.MaximumBytes)));
        // A backup whose checks were all removed stays without checks.
        backup.Checks.Clear();
        Assert.Empty(Read(JsonSerializer.Serialize(backup, App), "service").Checks);
    }

    [Fact]
    public void The_engine_is_never_asked_to_check_inside_a_backup()
    {
        Assert.NotNull(new Backup().CheckSchedule);
        Assert.NotNull(Read("""{"name":"old","checkSchedule":null}""", "app").CheckSchedule);
    }

    [Fact]
    public void New_backups_get_the_approved_defaults()
    {
        var backup = new Backup();
        BackupMaintenanceDefaults.ApplyTo(backup);
        Assert.Equal(ScheduleFrequency.Weekly, backup.PruneSchedule!.Frequency);
        Assert.Equal(0b0000_0001, backup.PruneSchedule.WeeklyDaysBitmask); // Once a week, not every day.
        // No separate structure check: the data sample checks the structure too.
        Assert.Equal(new[] { CheckKind.DataSample, CheckKind.TestRestore }, backup.Checks.Select(c => c.Kind));
        Assert.Equal(ScheduleFrequency.Weekly, backup.Checks[0].Schedule!.Frequency);
        Assert.Equal(BackupCheck.OneGiB, backup.Checks[0].MaximumBytes);
        Assert.Equal(ScheduleFrequency.Monthly, backup.Checks[1].Schedule!.Frequency);
        Assert.Equal(1, backup.Checks[1].SamplePercent);
        Assert.Equal(BackupCheck.OneGiB, backup.Checks[1].MaximumBytes);
        Assert.DoesNotContain(backup.Checks, c => c.Kind == CheckKind.FullDataRead);
        Assert.All(backup.Checks, c => Assert.Equal(TimeSpan.FromHours(3), c.Schedule!.TimeOfDay));
    }

    [Fact]
    public void Easy_mode_says_it_in_one_plain_sentence()
    {
        var backup = new Backup();
        BackupMaintenanceDefaults.ApplyTo(backup);
        Assert.Equal("Every week Backspace removes old copies it no longer needs to keep and checks that the backup is complete, "
            + "reading back 1 GiB of the stored data to make sure it is undamaged; every month it restores a few files as a test "
            + "to prove they come back.", BackupCheck.Sentence(backup));
        backup.PruneSchedule = null;
        backup.Checks = [new BackupCheck { Kind = CheckKind.Structure }];
        Assert.Equal("After each backup Backspace removes old copies it no longer needs to keep and checks that the backup is complete.",
            BackupCheck.Sentence(backup));
    }

    [Fact]
    public void Yearly_checks_run_on_the_first_of_january()
    {
        var yearly = new BackupSchedule { Frequency = ScheduleFrequency.Yearly, TimeOfDay = TimeSpan.FromHours(3) };
        Assert.Equal(new DateTime(2027, 1, 1, 3, 0, 0), yearly.ComputeNextFire(new DateTime(2026, 11, 10, 12, 0, 0)));
        Assert.Equal(new DateTime(2027, 1, 1, 3, 0, 0), yearly.ComputeNextFire(new DateTime(2027, 1, 1, 2, 0, 0)));
        Assert.Equal(new DateTime(2028, 1, 1, 3, 0, 0), yearly.ComputeNextFire(new DateTime(2027, 1, 1, 3, 0, 0)));
    }

    [Fact]
    public void Each_check_is_its_own_scheduled_entry_in_run_order()
    {
        var backup = new Backup { Id = "b" };
        var restore = new BackupCheck { Kind = CheckKind.TestRestore, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Weekly } };
        var sample = new BackupCheck { Kind = CheckKind.DataSample, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Weekly } };
        var structure = new BackupCheck { Kind = CheckKind.Structure, Schedule = new BackupSchedule { Frequency = ScheduleFrequency.Weekly } };
        var afterBackup = new BackupCheck { Kind = CheckKind.Structure, Schedule = null };
        backup.Checks = [restore, sample, afterBackup, structure];
        var plans = SchedulePlan.From(new AppConfig { Backups = { backup } }).Where(p => p.Kind == ScheduledTaskKind.Check).ToList();
        Assert.Equal(new[] { structure, sample, restore }, plans.Select(p => p.Check));
        Assert.Equal(3, plans.Select(p => p.Key).Distinct().Count());
        Assert.Equal("b/Check/" + structure.Id, plans[0].Key);
    }

    [Fact]
    public void Run_summaries_are_plain()
    {
        var check = new BackupCheck { Kind = CheckKind.DataSample };
        var start = new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc);
        var run = new RunRecord { StartedUtc = start, EndedUtc = start.AddMinutes(7).AddSeconds(50) };
        run.Cells.Add(new CellOutcome { DestinationName = "Google Drive", Status = BackupRunStatus.Success, Summary = "Backup check result: STATUS_SUCCESS." });
        Assert.Equal("Data sample (1 GiB) passed in 7 min 50 s.", CheckRunWording.Summary(check, run, [], metered: false));
        run.Cells[0].Status = BackupRunStatus.Failed; run.Cells[0].LastError = "pack 1a2b is damaged";
        Assert.Equal("Data sample (1 GiB) did not pass: pack 1a2b is damaged.", CheckRunWording.Summary(check, run, [], metered: false));
        var waiting = new RunRecord { StartedUtc = start, EndedUtc = start };
        waiting.Cells.Add(new CellOutcome { DestinationName = "My Passport", Status = BackupRunStatus.Skipped, DestinationUnavailable = true });
        Assert.Equal("Data sample (1 GiB) waits for «My Passport»: it runs by itself right after the next backup there.",
            CheckRunWording.Summary(check, waiting, ["My Passport"], metered: false));
        Assert.Equal("45 s", CheckRunWording.Duration(TimeSpan.FromSeconds(45)));
        Assert.Equal("20 min 1 s", CheckRunWording.Duration(TimeSpan.FromSeconds(1201.3)));
        Assert.Equal("1 h 05 min", CheckRunWording.Duration(TimeSpan.FromMinutes(65)));
        Assert.DoesNotContain("parts", CheckRunWording.Summary(check, run, [], metered: false));
    }

    [Fact]
    public void The_card_lists_each_checks_latest_result()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var backup = new Backup { Id = "b", Enabled = true, CreatedUtc = now.AddDays(-30) };
        BackupMaintenanceDefaults.ApplyTo(backup);
        backup.Checks.Insert(0, new BackupCheck { Kind = CheckKind.Structure, Schedule = backup.Checks[0].Schedule });
        var entries = new[]
        {
            new BackupCheckEntry("1", now.AddMinutes(-90), BackupRunStatus.Success, CheckDepth.SavedDataSample, ""),
            new BackupCheckEntry("2", now.AddHours(-3), BackupRunStatus.Failed, CheckDepth.RestoreSample, ""),
        };
        var lines = BackupCheckHistory.KindLines(backup, entries, now);
        // A data sample also checks the structure.
        Assert.Equal(new[] { "Structure check · passed 1h ago", "Data sample · passed 1h ago", "Sample test restore · failed 3h ago" }, lines);
        // A weekly check last run 20 days ago is due again.
        var old = new[] { new BackupCheckEntry("3", now.AddDays(-20), BackupRunStatus.Success, CheckDepth.RepositoryStructure, "") };
        Assert.Equal("Structure check · passed 20d ago · due again", BackupCheckHistory.KindLines(backup, old, now)[0]);
        Assert.Equal("Data sample · not run yet", BackupCheckHistory.KindLines(backup, old, now)[1]);
        Assert.Equal(lines, BackupCheckHistory.State(backup, entries, now).Kinds);
        // One line on the card in the Checks list's names; a failure wins over a later pass; each check in the hover.
        var state = BackupCheckHistory.State(backup, entries, now);
        Assert.Equal("Sample test restore failed · 3h ago", state.Label);
        Assert.EndsWith("Each check:\n" + string.Join("\n", lines), state.Details);
        backup.Checks.RemoveAll(c => c.Kind == CheckKind.TestRestore);
        var ok = BackupCheckHistory.State(backup, new[] { entries[0] }, now);
        Assert.Equal(("Data sample passed · 1h ago", "ok"), (ok.Label, ok.Tone));
    }
}
