using System;
using System.IO;
using System.Linq;
using Backspace.Models;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U061: backups to an external drive run in the background service. A
/// connected drive's folder is backed up, an unplugged one is left out (the
/// run skips it), and a missing folder on a connected drive is named.
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
        Assert.Equal(ServiceInstallationSecurity.CanonicalLocalPath(folder), ServiceExternalDrives.ConnectedPath(drive));

        drive.ExpectedDriveLetter = UnusedLetter;
        drive.PathOrSubpath = UnusedLetter + @"\Backup\Backspace\local_dropbox";
        Assert.Null(ServiceExternalDrives.ConnectedPath(drive));
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
    public void The_access_probe_leaves_nothing_behind()
    {
        using var ws = new TempWorkspace();
        var target = ws.Sub("Backup");
        Assert.True(ServiceExternalDrives.CanWrite(target));
        Assert.Empty(Directory.GetFiles(target));
        Assert.False(ServiceExternalDrives.CanWrite(Path.Combine(ws.Root, "not-made-yet")));
    }
}
