using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// D-7 / D-8: pin the two "cheap pre-flight" gates that keep restic
/// from burning minutes of retry against destinations we already know
/// are offline.
///
///  D-7 NetworkShare: <c>\\nonexistent\share</c> resolved through
///       <see cref="BackupOrchestrator.ProbeReachabilityAsync"/>
///       returns a Skipped CellOutcome rather than letting restic
///       drive its own ~13 minute exponential backoff.
///  D-8 ExternalDrive: a Z: drive that's not attached returns
///       <see cref="VolumeDetector.Verdict.DriveNotAttached"/> so
///       the cell skips before even reaching the reachability gate.
///
/// Both gates are user-visible: the gold-standard UX for "I unplugged
/// my external drive" or "I disconnected from my home VPN" is a
/// SECONDS-fast Skipped card with a readable reason, not a six-minute
/// stall and a red bar at the end. These pins guard the predicate so
/// a refactor of the cell-loop entry can't silently drop either gate.
/// </summary>
public class ReachabilityGateTests
{
    [Fact]
    public async Task NetworkShare_NonexistentUnc_ReturnsSkippedCellOutcome()
    {
        // A nonexistent host produces a known network-path outage or hits
        // the five-second deadline. Both are tolerable skips. The production
        // probe uses GetAttributes so permission/missing-folder failures
        // remain distinguishable from an unavailable host.
        var dest = new Destination
        {
            Name = "Offline NAS",
            Kind = DestinationKind.NetworkShare,
            PathOrSubpath = @"\\backspace-tests-nonexistent-host\share",
        };
        var miss = await BackupOrchestrator.ProbeReachabilityAsync(
            dest, sourcePath: @"C:\src", storageName: "default", ct: CancellationToken.None);
        Assert.NotNull(miss);
        Assert.Equal(BackupRunStatus.Skipped, miss!.Status);
        Assert.Contains("Network share unavailable", miss.Summary);
        Assert.Contains(@"\\backspace-tests-nonexistent-host\share", miss.Summary);
        Assert.False(string.IsNullOrWhiteSpace(miss.LastError));
    }

    [Fact]
    public async Task NetworkShare_EmptyPath_GateDoesNotApply()
    {
        // A NetworkShare destination with no path is invalid config —
        // the form validator catches it upstream — but the gate must
        // not crash on it; it should pass through (null) so the
        // downstream cell can surface a coherent error rather than
        // the gate raising a "share unreachable" red herring.
        var dest = new Destination
        {
            Name = "Empty NAS",
            Kind = DestinationKind.NetworkShare,
            PathOrSubpath = "",
        };
        var miss = await BackupOrchestrator.ProbeReachabilityAsync(
            dest, sourcePath: @"C:\src", storageName: "default", ct: CancellationToken.None);
        Assert.Null(miss);
    }

    [Fact]
    public async Task LocalFolder_NotInGate_ReturnsNull()
    {
        // LocalFolder is handled by Directory.Exists upstream in the
        // VolumeDetector + path-validation paths; the reachability
        // gate must NOT apply to it (LocalFolder failure shapes are
        // different — missing folder = Failed, not Skipped).
        var dest = new Destination
        {
            Name = "Local",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = @"C:\Backups",
        };
        var miss = await BackupOrchestrator.ProbeReachabilityAsync(
            dest, sourcePath: @"C:\src", storageName: "default", ct: CancellationToken.None);
        Assert.Null(miss);
    }

    [Fact]
    public async Task DropboxAppScoped_NotInGate_ReturnsNull()
    {
        // Cloud destinations don't get a cheap probe — restic's own
        // connectivity check carries the load there. The gate must
        // skip cloud kinds explicitly so a missing rclone remote
        // doesn't mask as "Network share unreachable" in the UI.
        var dest = new Destination
        {
            Name = "Dropbox",
            Kind = DestinationKind.DropboxAppScoped,
            PathOrSubpath = "/Backups",
        };
        var miss = await BackupOrchestrator.ProbeReachabilityAsync(
            dest, sourcePath: @"C:\src", storageName: "default", ct: CancellationToken.None);
        Assert.Null(miss);
    }

    // ---- D-8: ExternalDrive volume gate ----------------------------

    [Fact]
    public void ExternalDrive_ZDriveNotAttached_ReturnsDriveNotAttached()
    {
        // Z: by convention is the network-mapping letter on Windows
        // — it's reliably ABSENT on CI runners. If a developer
        // actually has Z: mapped this test will return WrongVolumeLabel
        // instead (which is also a non-Mounted verdict). Either way
        // the cell loop skips.
        if (!OperatingSystem.IsWindows()) return; // Drive-letter gate is Windows-only.
        var result = VolumeDetector.Check(expectedDriveLetter: "Z:", expectedVolumeLabel: "BackspaceCITestNonExistent");
        Assert.NotEqual(VolumeDetector.Verdict.Mounted, result.Verdict);
    }

    [Fact]
    public void ExternalDrive_VolumeLabelOnlyMissing_ReturnsDriveNotAttached()
    {
        // The label-only path scans every drive for a matching label.
        // A label nothing on this machine could have must return
        // DriveNotAttached, NOT Mounted. Regression guard: an earlier
        // refactor flipped the loop's continue/return and silently
        // returned Mounted for any unmatched scan.
        if (!OperatingSystem.IsWindows()) return;
        var result = VolumeDetector.Check(
            expectedDriveLetter: null,
            expectedVolumeLabel: "BackspaceCITestNonExistent_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Assert.Equal(VolumeDetector.Verdict.DriveNotAttached, result.Verdict);
    }

    [Fact]
    public void ExternalDrive_NeitherLetterNorLabel_ReturnsMounted()
    {
        // The "nothing requested" path: a destination with no
        // expected letter and no expected label means the user
        // didn't opt into the gate. Must return Mounted so the cell
        // runs (the rest of the path validation handles the actual
        // mount check via Directory.Exists upstream).
        var result = VolumeDetector.Check(expectedDriveLetter: "", expectedVolumeLabel: "");
        Assert.Equal(VolumeDetector.Verdict.Mounted, result.Verdict);
    }

    [Fact]
    public void ExternalDrive_LetterMatchesButLabelDoesNot_ReturnsWrongVolumeLabel()
    {
        // The "wrong drive plugged in" scenario: user labels their
        // backup drive "WD-2TB" expecting that exact one, plugs in a
        // different drive at the same letter. The gate must catch
        // this — silently writing to whatever's at that letter would
        // mix two unrelated backup sets, and the user would only
        // notice once the snapshot list looked wrong.
        if (!OperatingSystem.IsWindows()) return;
        // Find the first ready drive and inspect its actual label so
        // we can craft a guaranteed-mismatch label without false
        // negatives on CI where C:\ may or may not have a label.
        var ready = System.IO.DriveInfo.GetDrives();
        var anyReady = Array.Find(ready, d => d.IsReady && d.DriveType == System.IO.DriveType.Fixed);
        if (anyReady is null) return; // CI runner with no fixed drives? Skip.
        var bogusLabel = "BackspaceCITestNeverMatches_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var letter = anyReady.Name.TrimEnd('\\');
        var result = VolumeDetector.Check(letter, bogusLabel);
        Assert.Equal(VolumeDetector.Verdict.WrongVolumeLabel, result.Verdict);
    }
}
