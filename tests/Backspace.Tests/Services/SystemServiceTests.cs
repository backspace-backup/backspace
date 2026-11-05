using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>The version-3 background service (U100): Windows' own account,
/// one per Windows account, no permission changes, Quit stops it.</summary>
public sealed class SystemServiceTests
{
    private const string Owner = "S-1-5-21-1000000000-2000000000-3000000000-1001";

    [Fact]
    public void One_service_per_Windows_account_with_a_stable_name()
    {
        var name = SystemService.NameFor(Owner);
        Assert.Matches("^Backspace-[0-9a-f]{12}$", name);
        Assert.Equal(name, SystemService.NameFor(Owner));
        Assert.NotEqual(name, SystemService.NameFor("S-1-5-21-1000000000-2000000000-3000000000-1002"));
    }

    [Fact]
    public void Its_command_is_this_portable_executable_only()
    {
        var name = SystemService.NameFor(Owner);
        var command = SystemService.Command(@"C:\Users\me\Dropbox\Backspace\Backspace.exe", name);
        Assert.Equal($"\"C:\\Users\\me\\Dropbox\\Backspace\\Backspace.exe\" --system-service {name}", command);
        Assert.Equal(@"C:\Users\me\Dropbox\Backspace\Backspace.exe", SystemService.ExecutableFrom(command, name));
        // Another program, another service name or extra arguments are not this copy.
        Assert.Null(SystemService.ExecutableFrom(command.Replace("Backspace.exe", "evil.exe"), name));
        Assert.Null(SystemService.ExecutableFrom(command, SystemService.NameFor("S-1-5-21-1-2-3-4")));
        Assert.Null(SystemService.ExecutableFrom(command + " --extra", name));
        Assert.Null(SystemService.ExecutableFrom(@"C:\Backspace\Backspace.exe --system-service " + name, name));
        Assert.Throws<System.Security.SecurityException>(() => SystemService.Command(@"C:\Tools\other.exe", name));
    }

    [Fact]
    public void Its_working_files_stay_in_Backspace_config()
        => Assert.Equal(@"C:\Backspace\Backspace.config\service", SystemService.DataDirectoryFor(@"C:\Backspace"));

    [Fact]
    public void Its_binding_uses_Windows_own_account_for_endpoints()
    {
        var binding = new SystemService.Registration(SystemService.NameFor(Owner), Owner, @"C:\Backspace\Backspace.exe",
            @"C:\Users\me\AppData\Local\Backspace\bin", Guid.NewGuid().ToString("N"), true).Binding;
        Assert.True(binding.IsSystem);
        Assert.True(binding.CanRun);
        Assert.Equal("S-1-5-18", binding.ServiceSid);
        Assert.Equal(@"C:\Backspace", binding.Installation);
        // The owner, and only the owner, may connect to its pipes.
        var descriptor = BackupServicePipe.SecurityDescriptor(binding.ServiceSid, Owner);
        Assert.Contains("O:S-1-5-18", descriptor);
        Assert.Contains(Owner, descriptor);
        Assert.DoesNotContain(";;;WD)", descriptor);
        Assert.DoesNotContain(";;;AU)", descriptor);
    }

    [Fact]
    public void A_version_3_binding_is_never_written_into_the_old_record_format()
    {
        var v2 = new ServiceWorkerBinding(2, "Backspace-" + Guid.NewGuid().ToString("N"), Owner, @"C:\B\Backspace.config\windows-service\data", true);
        var json = JsonSerializer.Serialize(v2, ServiceRunAuthorization.Json);
        Assert.DoesNotContain("installation", json);
        Assert.DoesNotContain("engineRoot", json);
        Assert.Equal(v2, JsonSerializer.Deserialize<ServiceWorkerBinding>(json, ServiceRunAuthorization.Json));
    }

    private static InteractiveSessionIdentity Session(long logon = 100) => new(2, logon, "PC\\ME");

    private static string QuitRecord(TempWorkspace ws, uint? boot = 7, long logon = 100)
    {
        var dir = ws.Sub("service");
        File.WriteAllText(SystemServiceLifetime.PathFor(dir),
            JsonSerializer.Serialize(new SystemServiceLifetime.QuitRecord(1, 2, logon, "PC\\ME", boot, DateTime.UtcNow)));
        return dir;
    }

    [Fact]
    public void Without_a_Quit_the_service_runs()
    {
        using var ws = new TempWorkspace();
        Assert.False(SystemServiceLifetime.StaysQuit(ws.Sub("service"), 7, _ => Session(), out _));
    }

    [Fact]
    public void After_Quit_it_stays_stopped_in_the_same_sign_in_including_after_sleep_or_hibernation()
    {
        using var ws = new TempWorkspace();
        var dir = QuitRecord(ws);
        Assert.True(SystemServiceLifetime.StaysQuit(dir, 7, _ => Session(), out var reason));
        Assert.Contains("quit", reason);
        Assert.True(File.Exists(SystemServiceLifetime.PathFor(dir)));
    }

    /// <summary>U121 (4 November 2026): opening 1.1.4 said scheduled tasks
    /// stayed paused because the Quit record was "being used by another
    /// process": the service reading it as it started, or Dropbox syncing it
    /// in the portable folder. Opening Backspace now waits out such a reader.</summary>
    [Fact]
    public async Task Opening_Backspace_removes_the_Quit_record_even_while_something_reads_it()
    {
        using var ws = new TempWorkspace();
        var dir = QuitRecord(ws);
        var path = SystemServiceLifetime.PathFor(dir);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // like Dropbox: no delete sharing
        // Without waiting, Windows refuses (what the person saw).
        Assert.Throws<IOException>(() => File.Delete(path));
        var release = Task.Run(async () => { await Task.Delay(800); held.Dispose(); });
        SystemServiceLifetime.DeleteRecord(path);
        await release;
        Assert.False(File.Exists(path));
        SystemServiceLifetime.DeleteRecord(path); // already gone: nothing to do
        // A lasting lock is waited out until the operation is stopped (U135).
        var again = QuitRecord(ws);
        using (new FileStream(SystemServiceLifetime.PathFor(again), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var stop = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            Assert.ThrowsAny<OperationCanceledException>(() => SystemServiceLifetime.DeleteRecord(SystemServiceLifetime.PathFor(again), stop.Token));
    }

    [Fact]
    public void The_service_reading_the_Quit_record_does_not_stop_Backspace_from_removing_it()
    {
        using var ws = new TempWorkspace();
        var dir = QuitRecord(ws);
        // The service reads it this way while it starts.
        using var reading = new FileStream(SystemServiceLifetime.PathFor(dir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        File.Delete(SystemServiceLifetime.PathFor(dir));
        Assert.False(File.Exists(SystemServiceLifetime.PathFor(dir)));
        // Removed between the check and the read: Backspace was just opened, so the service runs.
        Assert.False(SystemServiceLifetime.StaysQuit(dir, 7, _ => Session(), out _));
    }

    /// <summary>U131 (4 November 2026): after the PC went down during a
    /// backup, the notification said only "The background worker stopped
    /// unexpectedly". A run that Windows started after now says why.</summary>
    [Fact]
    public void A_backup_cut_short_by_a_restart_says_so()
    {
        var started = new DateTime(2026, 11, 4, 13, 6, 32, DateTimeKind.Utc);
        Assert.StartsWith("The computer restarted, shut down or lost power during this backup",
            SystemBackupService.InterruptionReason(started, started.AddHours(6.8)));
        // Windows was already running: the worker itself stopped, and the
        // general wording stays.
        Assert.Null(SystemBackupService.InterruptionReason(started, started.AddDays(-1)));
        // Resumed by itself (U131): recorded as interrupted, for either cause.
        Assert.StartsWith("Interrupted: the computer restarted", SystemBackupService.InterruptionReason(started, started.AddHours(6.8), resumes: true));
        Assert.StartsWith("Interrupted: Backspace's background service stopped", SystemBackupService.InterruptionReason(started, started.AddDays(-1), resumes: true));
    }

    [Fact]
    public void A_restart_ends_the_Quit()
    {
        using var ws = new TempWorkspace();
        var dir = QuitRecord(ws);
        Assert.False(SystemServiceLifetime.StaysQuit(dir, 8, _ => Session(), out _));
        Assert.False(File.Exists(SystemServiceLifetime.PathFor(dir)));
    }

    [Fact]
    public void Shut_down_with_Fast_Startup_ends_the_Quit_because_it_signs_everyone_out()
    {
        using var ws = new TempWorkspace();
        // Same Windows start (Fast Startup resumes it), but the session is gone.
        var dir = QuitRecord(ws);
        Assert.False(SystemServiceLifetime.StaysQuit(dir, 7, _ => null, out _));
        // Signed in again with the same session number: a new sign-in.
        dir = QuitRecord(ws);
        Assert.False(SystemServiceLifetime.StaysQuit(dir, 7, _ => Session(logon: 200), out _));
    }

    [Fact]
    public void An_unreadable_Quit_record_keeps_the_service_stopped()
    {
        using var ws = new TempWorkspace();
        var dir = ws.Sub("service");
        File.WriteAllText(SystemServiceLifetime.PathFor(dir), "{not json");
        Assert.True(SystemServiceLifetime.StaysQuit(dir, 7, _ => Session(), out _));
        Assert.True(SystemServiceLifetime.StaysQuit(QuitRecord(ws), 7, _ => throw new InvalidOperationException("no access"), out _));
    }

    private static AppConfig Config(Action<Backup>? change = null)
    {
        var destination = new Destination { Id = "d1", Name = "Disk", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\Backups" };
        var backup = new Backup
        {
            Id = "b1", Name = "Documents", Enabled = true, SourcePaths = [@"C:\Users\me\Documents"],
            Targets = [new BackupTarget { DestinationId = "d1" }], Schedule = new BackupSchedule(),
        };
        change?.Invoke(backup);
        return new AppConfig { Backups = [backup], Destinations = [destination] };
    }

    [Fact]
    public void The_service_runs_the_backups_as_saved()
    {
        var run = SystemServiceRuns.Authorize(Config(), "b1", "backup", ownerCommand: false);
        Assert.Equal("b1", run.Backup.Id);
        Assert.Equal(RunKind.Backup, run.Kind);
        // The owner may check or clean up on demand; the schedule only when planned.
        Assert.Equal(RunKind.Check, SystemServiceRuns.Authorize(Config(), "b1", "check", ownerCommand: true).Kind);
        Assert.Throws<System.Security.SecurityException>(() => SystemServiceRuns.Authorize(Config(), "b1", "check", ownerCommand: false));
        Assert.Throws<System.Security.SecurityException>(() => SystemServiceRuns.Authorize(Config(), "b1", "restore", ownerCommand: true));
    }

    [Fact]
    public void Backups_the_service_cannot_run_stay_with_the_window()
    {
        Assert.False(SystemServiceRuns.CanRun(Config(b => b.Enabled = false).Backups[0], Config(b => b.Enabled = false)));
        var custom = Config(b => b.ExtraBackupArgs = "--one-file-system");
        Assert.False(SystemServiceRuns.CanRun(custom.Backups[0], custom));
        Assert.Throws<NotSupportedException>(() => SystemServiceRuns.Authorize(custom, "b1", "backup", ownerCommand: true));
        var noSources = Config(b => b.SourcePaths = []);
        Assert.False(SystemServiceRuns.CanRun(noSources.Backups[0], noSources));
        Assert.Throws<InvalidDataException>(() => SystemServiceRuns.Authorize(Config(), "missing", "backup", ownerCommand: true));
        var share = Config();
        share.Destinations[0] = new Destination { Id = "d1", Name = "NAS", Kind = DestinationKind.NetworkShare, PathOrSubpath = @"\\nas\backups" };
        Assert.False(SystemServiceRuns.CanRun(share.Backups[0], share));
    }

    [Fact]
    public void The_upgrade_keeps_the_earlier_service_history_and_its_transcripts()
    {
        using var ws = new TempWorkspace();
        var oldLogs = ws.Sub(@"old\logs\engine");
        var oldStore = new LogStore(oldLogs);
        var transcript = Path.Combine(oldLogs, "Documents", "20261101-020000-backup.log");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, "restic output");
        var run = new RunRecord { Id = Guid.NewGuid().ToString("N"), BackupId = "b1", BackupName = "Documents",
            StartedUtc = DateTime.UtcNow.AddHours(-3), Status = BackupRunStatus.Success, LogPath = transcript };
        oldStore.AppendRun(run);
        var newLogs = Path.Combine(ws.Root, "new", "logs", "engine");

        SystemServiceUpgrade.CopyHistory(oldLogs, newLogs);
        SystemServiceUpgrade.CopyHistory(oldLogs, newLogs); // Retried: nothing is doubled.

        var copied = new LogStore(newLogs).LoadHistory("Documents");
        var single = Assert.Single(copied);
        Assert.Equal(run.Id, single.Id);
        Assert.Equal(Path.Combine(newLogs, "Documents", "20261101-020000-backup.log"), single.LogPath);
        Assert.Equal("restic output", File.ReadAllText(single.LogPath));
    }
}
