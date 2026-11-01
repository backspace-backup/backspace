using System;
using System.IO;
using System.Linq;
using System.Threading;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U061: backups to an external drive run in the background service. A
/// connected drive's folder is backed up, an unplugged one is left out (the
/// run skips it), and a folder the service cannot write is prepared by the
/// settings task before the run starts.
/// </summary>
public sealed class ServiceExternalDrivesTests
{
    private static string SystemLetter => Path.GetPathRoot(Environment.SystemDirectory)!.Substring(0, 2);
    private static string UnusedLetter => "QRSTUVWXYZ".Select(c => c + ":").First(l => !new DriveInfo(l).IsReady);

    private static (AppConfig Config, Backup Backup, Destination Drive) Setup(string letter, string folder)
    {
        var drive = new Destination { Name = "My Passport", Kind = DestinationKind.ExternalDrive, ExpectedDriveLetter = letter, PathOrSubpath = folder };
        var backup = new Backup { Id = "b", Name = "Dropbox folder", SourcePaths = { @"C:\Users\me\Dropbox" } };
        backup.Targets.Add(new BackupTarget { DestinationId = drive.Id });
        var config = new AppConfig(); config.Destinations.Add(drive); config.Backups.Add(backup);
        return (config, backup, drive);
    }

    [Fact]
    public void A_connected_drive_is_backed_up_and_an_unplugged_one_is_left_out()
    {
        using var ws = new TempWorkspace();
        var folder = ws.Sub("Backup");
        var (config, backup, drive) = Setup(SystemLetter, folder);
        var run = new ServiceRunAuthorization.AuthorizedRun("r", backup, config, RunKind.Backup);
        Assert.Contains(ServiceInstallationSecurity.CanonicalLocalPath(folder), ServiceApprovedWorker.LocalPathsFor(run));
        Assert.Equal(new[] { ServiceInstallationSecurity.CanonicalLocalPath(folder) }, ServiceExternalDrives.ConnectedRoots(config));

        drive.ExpectedDriveLetter = UnusedLetter;
        drive.PathOrSubpath = UnusedLetter + @"\Backup\Backspace\local_dropbox";
        Assert.DoesNotContain(ServiceApprovedWorker.LocalPathsFor(run), p => p.StartsWith(UnusedLetter, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(ServiceExternalDrives.ConnectedRoots(config));
        // Unplugged is not a failure the host has to explain: the run skips it.
        Assert.Null(ServiceApprovedWorker.MissingDestination(run));
    }

    [Fact]
    public void A_drive_with_the_wrong_label_counts_as_unplugged()
    {
        using var ws = new TempWorkspace();
        var (_, _, drive) = Setup(SystemLetter, ws.Sub("Backup"));
        drive.ExpectedVolumeLabel = "Not this drive " + Guid.NewGuid().ToString("N");
        Assert.Null(ServiceExternalDrives.ConnectedPath(drive));
    }

    [Fact]
    public void A_missing_folder_on_a_connected_drive_is_named()
    {
        using var ws = new TempWorkspace();
        var (config, backup, _) = Setup(SystemLetter, Path.Combine(ws.Root, "not-made-yet"));
        var reason = ServiceApprovedWorker.MissingDestination(new("r", backup, config, RunKind.Backup));
        Assert.Contains("«My Passport»", reason);
        Assert.Contains("missing on the drive", reason);
    }

    [Fact]
    public void The_settings_task_is_asked_only_when_the_folder_cannot_be_written()
    {
        using var ws = new TempWorkspace();
        var target = Path.Combine(ws.Root, "Backup");
        var (config, backup, _) = Setup(SystemLetter, target);
        var binding = (ServiceWorkerBinding)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ServiceWorkerBinding));
        var asked = 0;
        // Not there yet: the task is asked; it makes the folder, as SYSTEM does.
        var left = ServiceExternalDrives.EnsureAccess(binding, backup, config, TimeSpan.FromSeconds(20), CancellationToken.None,
            _ => { asked++; Directory.CreateDirectory(target); return true; });
        Assert.Equal(1, asked);
        Assert.Empty(left);
        // Writable now: nothing is asked.
        ServiceExternalDrives.EnsureAccess(binding, backup, config, TimeSpan.FromSeconds(5), CancellationToken.None, _ => { asked++; return true; });
        Assert.Equal(1, asked);
        Assert.True(ServiceExternalDrives.CanWrite(target));
        Assert.Empty(Directory.GetFiles(target)); // the access probe leaves nothing behind
    }
}
