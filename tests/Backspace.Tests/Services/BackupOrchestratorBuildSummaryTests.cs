using System;
using System.Linq;
using Backspace.Models;
using Backspace.Services;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// Pins the run-summary phrasing the BackupCard renders behind
/// "LAST". The user reported: "when a backup is completed, the line
/// LAST in 6m 10s isn't very clear. It'd be best to show LAST
/// 'Completed in 4m 10s'." — that's the no-op success path where
/// nothing was uploaded and no files changed; the only piece of
/// useful info is the duration, and "in 6m 10s" reads as a
/// preposition with no verb.
/// </summary>
public class BackupOrchestratorBuildSummaryTests
{
    [Fact]
    public void NoOpSuccessRun_ReadsAsCompletedInDuration()
    {
        var r = new RunRecord
        {
            BackupId = "b1",
            BackupName = "test",
            Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddMinutes(-6).AddSeconds(-10),
            EndedUtc   = DateTime.UtcNow,
            BytesUploaded = 0,
            FilesNew = 0, FilesModified = 0, FilesRemoved = 0,
        };
        var s = BackupOrchestrator.BuildSummary(r);
        Assert.StartsWith("Completed in ", s);
        // No leading "in " preposition orphaned at the start.
        Assert.False(s.StartsWith("in "), $"Got '{s}' — bare 'in {{dur}}' regression.");
    }

    [Fact]
    public void RunWithUploadedBytes_StillUsesBareInDuration()
    {
        // When the leading parts already describe what happened,
        // "in {dur}" reads naturally — keep the existing phrasing.
        var r = new RunRecord
        {
            BackupId = "b1",
            BackupName = "test",
            Status = BackupRunStatus.Success,
            StartedUtc = DateTime.UtcNow.AddMinutes(-2),
            EndedUtc   = DateTime.UtcNow,
            BytesUploaded = 1234567,
            FilesNew = 5,
        };
        var s = BackupOrchestrator.BuildSummary(r);
        Assert.Contains("uploaded", s);
        Assert.Contains(" · in ", s);
        Assert.DoesNotContain("Completed in ", s);
    }

    [Fact]
    public void SkippedRun_StillUsesSkippedAfterPhrasing()
    {
        // The existing "Manually skipped after Xs" phrasing for
        // skipped runs must keep working; my no-op fix only affects
        // the success path.
        var r = new RunRecord
        {
            BackupId = "b1",
            BackupName = "test",
            Status = BackupRunStatus.Skipped,
            StartedUtc = DateTime.UtcNow.AddSeconds(-5),
            EndedUtc   = DateTime.UtcNow,
        };
        var s = BackupOrchestrator.BuildSummary(r, orchestratorCancelReason: "Manually skipped");
        Assert.Contains("Manually skipped", s);
        Assert.Contains("after ", s);
        Assert.DoesNotContain("Completed in ", s);
    }

    [Fact]
    public void SkippedRun_AppendsLocateMovedHint_WhenBackupHadPriorSuccessAndDestIsLocal()
    {
        // Cross-reference flow: a backup that USED to work but was
        // Skipped this time most likely has a relocated destination.
        // If the destination kind has the Locate moved button (Local /
        // External / Network), tell the user where to find it.
        var dest = new Destination
        {
            Id = "d1", Name = "Local Drive",
            Kind = DestinationKind.LocalFolder,
            PathOrSubpath = @"C:\Backups",
        };
        var backup = new Backup
        {
            Id = "b1", Name = "test",
            LastSuccessUtc = DateTime.UtcNow.AddDays(-1),
            Targets = { new BackupTarget { DestinationId = "d1", StorageName = "default" } },
        };
        var config = new AppConfig();
        config.Destinations.Add(dest);
        var r = new RunRecord
        {
            BackupId = "b1", BackupName = "test",
            Status = BackupRunStatus.Skipped,
            StartedUtc = DateTime.UtcNow.AddSeconds(-5),
            EndedUtc   = DateTime.UtcNow,
            Cells =
            {
                // A previously working local repository gets an optional
                // relocation hint, rather than an assertion that it moved.
                new CellOutcome { Status = BackupRunStatus.Skipped, DestinationName = "Local Drive", StorageName = "default", Summary = "Repository missing", LastError = "Fatal: repository does not exist (exit status 10)" },
            },
        };
        var s = BackupOrchestrator.BuildSummary(r, backup: backup, config: config);
        Assert.Contains("Locate moved repository", s);
    }

    [Fact]
    public void SkippedRun_ConditionalRelocationHint_WhenPreviouslyWorkingShareIsOffline()
    {
        // The user requested a pointer to relocation after previous
        // success. Conditional wording preserves the offline explanation.
        var dest = new Destination
        {
            Id = "d1", Name = "NAS",
            Kind = DestinationKind.NetworkShare,
            PathOrSubpath = @"\\nas\backups",
        };
        var backup = new Backup
        {
            Id = "b1", Name = "test",
            LastSuccessUtc = DateTime.UtcNow.AddDays(-1),
            Targets = { new BackupTarget { DestinationId = "d1", StorageName = "default" } },
        };
        var config = new AppConfig();
        config.Destinations.Add(dest);
        var r = new RunRecord
        {
            BackupId = "b1", BackupName = "test",
            Status = BackupRunStatus.Skipped,
            StartedUtc = DateTime.UtcNow.AddSeconds(-5),
            EndedUtc   = DateTime.UtcNow,
            Cells = { new CellOutcome { Status = BackupRunStatus.Skipped, DestinationName = "NAS", StorageName = "default", Summary = "Network share unreachable", LastError = "Network path was not found" } },
        };
        var s = BackupOrchestrator.BuildSummary(r, backup: backup, config: config);
        Assert.Contains("if you've moved this backup folder", s);
    }

    /// <summary>U061: an unplugged external drive has not moved; the hint says
    /// to connect it rather than to locate a moved repository.</summary>
    [Fact]
    public void SkippedRun_UnpluggedExternalDrive_SaysToConnectIt()
    {
        var letter = "QRSTUVWXYZ".Select(c => c + ":").First(l => !new System.IO.DriveInfo(l).IsReady);
        var dest = new Destination { Id = "d1", Name = "Passport", Kind = DestinationKind.ExternalDrive, ExpectedDriveLetter = letter, PathOrSubpath = letter + @"Backup" };
        var backup = new Backup { Id = "b1", Name = "test", LastSuccessUtc = DateTime.UtcNow.AddDays(-1),
            Targets = { new BackupTarget { DestinationId = "d1", StorageName = "default" } } };
        var config = new AppConfig(); config.Destinations.Add(dest);
        var r = new RunRecord { BackupId = "b1", BackupName = "test", Status = BackupRunStatus.Skipped,
            StartedUtc = DateTime.UtcNow.AddSeconds(-1), EndedUtc = DateTime.UtcNow,
            Cells = { new CellOutcome { Status = BackupRunStatus.Skipped, DestinationName = "Passport", StorageName = "default", Summary = letter + @" not mounted" } } };
        var s = BackupOrchestrator.BuildSummary(r, backup: backup, config: config);
        Assert.Contains("connect the drive", s);
        Assert.DoesNotContain("moved this backup folder", s);
    }

    [Fact]
    public void SkippedRun_NoHint_WhenBackupHasNeverSucceeded()
    {
        // A backup that's never succeeded is more likely mis-configured
        // than relocated — the hint would be misleading. Suppress.
        var dest = new Destination { Id = "d1", Name = "Local", Kind = DestinationKind.LocalFolder };
        var backup = new Backup { Id = "b1", Name = "test", LastSuccessUtc = null };
        var config = new AppConfig();
        config.Destinations.Add(dest);
        var r = new RunRecord
        {
            BackupId = "b1", BackupName = "test",
            Status = BackupRunStatus.Skipped,
            StartedUtc = DateTime.UtcNow.AddSeconds(-5),
            EndedUtc   = DateTime.UtcNow,
            Cells = { new CellOutcome { Status = BackupRunStatus.Skipped, DestinationName = "Local", Summary = "Network share unreachable" } },
        };
        var s = BackupOrchestrator.BuildSummary(r, backup: backup, config: config);
        Assert.DoesNotContain("Locate moved repository", s);
    }

    [Fact]
    public void SkippedRun_NoHint_WhenDestinationIsCloud()
    {
        // Cloud destinations have their own recovery path (Subpath
        // edit + re-auth); the Locate moved button isn't shown for
        // them, so the hint would point at a missing affordance.
        var dest = new Destination
        {
            Id = "d1", Name = "Dropbox",
            Kind = DestinationKind.DropboxAppScoped,
            PathOrSubpath = "/Backups",
        };
        var backup = new Backup
        {
            Id = "b1", Name = "test",
            LastSuccessUtc = DateTime.UtcNow.AddDays(-1),
            Targets = { new BackupTarget { DestinationId = "d1", StorageName = "default" } },
        };
        var config = new AppConfig();
        config.Destinations.Add(dest);
        var r = new RunRecord
        {
            BackupId = "b1", BackupName = "test",
            Status = BackupRunStatus.Skipped,
            StartedUtc = DateTime.UtcNow.AddSeconds(-5),
            EndedUtc   = DateTime.UtcNow,
            Cells = { new CellOutcome { Status = BackupRunStatus.Skipped, DestinationName = "Dropbox", Summary = "Cloud destination unreachable" } },
        };
        var s = BackupOrchestrator.BuildSummary(r, backup: backup, config: config);
        Assert.DoesNotContain("Locate moved repository", s);
    }
}
