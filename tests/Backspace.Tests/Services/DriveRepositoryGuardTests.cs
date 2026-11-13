using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U117: an external drive that lacks the backup is never started over
/// without the person's answer. The test "drive" is the system drive,
/// matched by its letter; the marker files go to a temporary folder
/// instead of the drive root.
/// </summary>
[Collection(Backspace.Tests.Scenarios.ScenarioCollection.Name)]
public sealed class DriveRepositoryGuardTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("drive-guard");
    private readonly string _markers;

    public DriveRepositoryGuardTests()
    {
        _markers = _workspace.Sub("drive-root");
        DriveRepositoryGuard.MarkerRootOverride = _markers;
    }

    public void Dispose()
    {
        DriveRepositoryGuard.MarkerRootOverride = null;
        DriveConfirmation.AskOverride = null;
        _workspace.Dispose();
    }

    private Destination Drive(string name = "repo", DestinationKind kind = DestinationKind.ExternalDrive) => new()
    {
        Name = kind == DestinationKind.NetworkShare ? "NAS" : "My Passport",
        Kind = kind,
        // A share is reached by its path alone; the test share is a local folder.
        ExpectedDriveLetter = kind == DestinationKind.ExternalDrive ? Path.GetPathRoot(_workspace.Root)![..2] : null,
        PathOrSubpath = Path.Combine(_workspace.Root, name),
    };

    private static void MakeRepository(Destination d)
    {
        Directory.CreateDirectory(d.PathOrSubpath);
        File.WriteAllText(Path.Combine(d.PathOrSubpath, "config"), "restic");
    }

    [Fact]
    public void Each_backup_decides_for_itself_and_the_repository_is_the_proof()
    {
        if (!OperatingSystem.IsWindows()) return;
        var drive = Drive();
        var mine = new Backup { Name = "local_dropbox" };
        var other = new Backup { Name = "photos" };

        Assert.Equal(DriveRepositoryGuard.State.Missing, DriveRepositoryGuard.Check(mine, drive));
        DriveRepositoryGuard.Decline(mine, drive);
        Assert.True(File.Exists(Path.Combine(_markers, $".backspace.{mine.Id}.declined")));
        Assert.Equal(DriveRepositoryGuard.State.Declined, DriveRepositoryGuard.Check(mine, drive));
        // Declining is per backup, not per drive or app.
        Assert.Equal(DriveRepositoryGuard.State.Missing, DriveRepositoryGuard.Check(other, drive));

        DriveRepositoryGuard.Accept(mine, drive);
        Assert.False(File.Exists(Path.Combine(_markers, $".backspace.{mine.Id}.declined")));
        Assert.Equal(DriveRepositoryGuard.State.Accepted, DriveRepositoryGuard.Check(mine, drive));

        MakeRepository(drive);
        Assert.Equal(DriveRepositoryGuard.State.Present, DriveRepositoryGuard.Check(mine, drive));
        DriveRepositoryGuard.ClearAccepted(mine, drive);
        Assert.Empty(Directory.GetFiles(_markers));

        // Not an external drive: the guard stays out of the way.
        Assert.Equal(DriveRepositoryGuard.State.NotApplicable,
            DriveRepositoryGuard.Check(mine, new Destination { Kind = DestinationKind.LocalFolder, PathOrSubpath = _workspace.Sub("local") }));
        // A drive that isn't attached is the volume check's business.
        Assert.Equal(DriveRepositoryGuard.State.NotApplicable,
            DriveRepositoryGuard.Check(mine, new Destination { Kind = DestinationKind.ExternalDrive, ExpectedVolumeLabel = "Nothing-" + Guid.NewGuid().ToString("N"), PathOrSubpath = @"M:\Backup" }));
    }

    [Theory]
    [InlineData(DestinationKind.ExternalDrive)]
    [InlineData(DestinationKind.NetworkShare)] // U117 follow-up, 4 November 2026
    public async Task An_unattended_run_never_starts_over_on_a_drive_without_the_backup(DestinationKind kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        var config = new ConfigStore(); config.Load();
        var logs = new LogStore();
        var drive = Drive(kind: kind);
        var backup = new Backup { Name = "local_dropbox", AbortOnMeteredNetwork = false, UseVss = false, VssDisabledAcknowledged = true,
            SourcePaths = { _workspace.Sub("source") }, Targets = { new() { DestinationId = drive.Id } } };
        config.Update(c => { c.Backups.Add(backup); c.Destinations.Add(drive);
            c.Notifications.OnSuccess = new(); c.Notifications.OnSkipped = new(); c.Notifications.OnFailure = new(); });
        var engine = DispatchProxy.Create<IBackupEngine, CountingEngine>();
        var counting = (CountingEngine)(object)engine;
        counting.OnBackup = () => MakeRepository(drive);
        using var http = new HttpClient(new OkHandler());
        var orchestrator = new BackupOrchestrator(config, new SecretsStore(), engine, logs, new HealthcheckService(config, new SecretsStore(), http),
            new MailService(config, new SecretsStore()), new NotificationService(config), new BackupProgressService());

        // No repository, no answer: the run fails (so alerts fire), and the engine is never asked.
        var waiting = await orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Failed, waiting.Status);
        Assert.StartsWith("Waiting for you:", Assert.Single(waiting.Cells).Summary);
        Assert.Equal(0, counting.Calls);

        // Declined: skipped for this backup with a way back.
        DriveRepositoryGuard.Decline(backup, drive);
        var declined = await orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Skipped, declined.Status);
        Assert.Contains("you chose not to back up", Assert.Single(declined.Cells).Summary);
        Assert.Equal(0, counting.Calls);

        // Accepted: the backup runs, and once the repository exists the go-ahead file goes.
        DriveRepositoryGuard.Accept(backup, drive);
        var first = await orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Success, first.Status);
        Assert.Equal(1, counting.Calls);
        Assert.Empty(Directory.GetFiles(_markers));

        // The repository is there: later runs need nothing.
        Assert.Equal(BackupRunStatus.Success, (await orchestrator.RunBackupAsync(backup, CancellationToken.None)).Status);
        // Erased drive (same name): fails again instead of starting over.
        Directory.Delete(drive.PathOrSubpath, recursive: true);
        var erased = await orchestrator.RunBackupAsync(backup, CancellationToken.None);
        Assert.Equal(BackupRunStatus.Failed, erased.Status);
        Assert.StartsWith("Waiting for you:", Assert.Single(erased.Cells).Summary);
        Assert.Equal(2, counting.Calls);
    }

    [Fact]
    public void Answers_go_to_the_root_and_then_beside_the_backup_folder()
    {
        PlatformTest.WindowsOnly("Windows paths (drive letters, \\\\server shares)");
        DriveRepositoryGuard.MarkerRootOverride = null;
        try
        {
            Assert.Equal(new[] { @"\\nas\backups", @"\\nas\backups\Backspace" },
                DriveRepositoryGuard.MarkerFolders(@"\\nas\backups\Backspace\local_c"));
            Assert.Equal(new[] { @"M:\", @"M:\Backup\Backspace" }, DriveRepositoryGuard.MarkerFolders(@"M:\Backup\Backspace\local_dropbox"));
            // A backup folder right at the root has one place only.
            Assert.Equal(new[] { @"M:\" }, DriveRepositoryGuard.MarkerFolders(@"M:\local_dropbox"));
        }
        finally { DriveRepositoryGuard.MarkerRootOverride = _markers; }
    }

    [Fact]
    public void A_share_that_does_not_answer_is_left_to_the_reachability_check()
    {
        if (!OperatingSystem.IsWindows()) return;
        var offline = new Destination { Kind = DestinationKind.NetworkShare, PathOrSubpath = @"\\backspace-tests-nonexistent-host\share\repo" };
        Assert.Equal(DriveRepositoryGuard.State.NotApplicable, DriveRepositoryGuard.Check(new Backup(), offline));
    }

    [Fact]
    public async Task Starting_a_first_backup_is_the_go_ahead_but_a_missing_backup_is_asked_about()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        var drive = Drive("asked");
        var backup = new Backup { Name = "local_dropbox", Targets = { new() { DestinationId = drive.Id } } };
        ServiceLocator.Config.Update(c => c.Destinations.Add(drive));
        try
        {
            DriveConfirmation.Question? asked = null;
            DriveConfirmation.AskOverride = q => { asked = q; return Task.FromResult(DriveChoiceWindow.Choice.Cancel); };

            // Never backed up there: no question, the go-ahead is recorded.
            Assert.True(await DriveConfirmation.ConfirmAsync(backup, null));
            Assert.Null(asked);
            Assert.Equal(DriveRepositoryGuard.State.Accepted, DriveRepositoryGuard.Check(backup, drive));
            File.Delete(Path.Combine(_markers, $".backspace.{backup.Id}.accepted"));

            // Backed up there before, now missing: asked; Not now runs nothing.
            backup.LastSuccessUtc = DateTime.UtcNow.AddDays(-1);
            Assert.False(await DriveConfirmation.ConfirmAsync(backup, null));
            Assert.Equal("This drive doesn't have this backup", asked!.Title);
            Assert.Equal(DriveRepositoryGuard.State.Missing, DriveRepositoryGuard.Check(backup, drive));

            DriveConfirmation.AskOverride = _ => Task.FromResult(DriveChoiceWindow.Choice.Decline);
            Assert.True(await DriveConfirmation.ConfirmAsync(backup, null));
            Assert.Equal(DriveRepositoryGuard.State.Declined, DriveRepositoryGuard.Check(backup, drive));

            // Run now on a declined drive offers to use it again.
            DriveConfirmation.AskOverride = q => { asked = q; return Task.FromResult(DriveChoiceWindow.Choice.UseDrive); };
            Assert.True(await DriveConfirmation.ConfirmAsync(backup, null));
            Assert.Null(asked.DeclineLabel);
            Assert.Equal(DriveRepositoryGuard.State.Accepted, DriveRepositoryGuard.Check(backup, drive));
        }
        finally { ServiceLocator.Config.Update(c => c.Destinations.RemoveAll(d => d.Id == drive.Id)); }
    }

    public class CountingEngine : DispatchProxy
    {
        private readonly InMemoryBackupEngine _other = new();
        public int Calls;
        public Action OnBackup { get; set; } = () => { };
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(IBackupEngine.BackupPruneCheckAsync)) return method.Invoke(_other, args);
            Calls++;
            OnBackup();
            return Task.FromResult(new RunRecord { Status = BackupRunStatus.Success, Summary = "ok" });
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
