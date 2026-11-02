using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U082: an imported Duplicacy destination has no cloud sign-in and no
/// storage password yet. A run must say what to finish instead of retrying
/// restic four times on "an empty password is not allowed".
/// </summary>
public sealed class DestinationReadinessTests
{
    private static SecretsStore Vault(TempWorkspace ws) => new(new DpapiSecretsEncryption(), Path.Combine(ws.Root, "secrets.bin"));

    [Fact]
    public void An_imported_dropbox_destination_needs_the_sign_in_and_a_storage_password()
    {
        using var ws = new TempWorkspace();
        var vault = Vault(ws);
        var imported = new Destination { Name = "Dropbox - erwin_local_c", Kind = DestinationKind.DropboxAppScoped, Encrypted = true, PathOrSubpath = "erwin_local_c" };
        Assert.Equal("«Dropbox - erwin_local_c» isn't set up yet: connect your Dropbox account and choose a storage password. Open it from Destinations to finish.",
            DestinationReadiness.Problem(imported, vault));

        imported.OAuthTokenRef = "dest:x:oauth"; vault.Set("dest:x:oauth", "{\"access_token\":\"t\"}");
        Assert.Contains("choose a storage password", DestinationReadiness.Problem(imported, vault));
        Assert.DoesNotContain("Dropbox account", DestinationReadiness.Problem(imported, vault));

        imported.StoragePasswordRef = "dest:x:pw"; vault.Set("dest:x:pw", "");
        Assert.NotNull(DestinationReadiness.Problem(imported, vault)); // an empty saved password is not a password
        vault.Set("dest:x:pw", "secret");
        Assert.Null(DestinationReadiness.Problem(imported, vault));
    }

    [Fact]
    public void Local_and_unencrypted_destinations_need_nothing_more()
    {
        using var ws = new TempWorkspace();
        var vault = Vault(ws);
        Assert.Null(DestinationReadiness.Problem(new Destination { Name = "Disk", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\Backups" }, vault));
        Assert.Equal("«S3» isn't set up yet: enter its access keys. Open it from Destinations to finish.",
            DestinationReadiness.Problem(new Destination { Name = "S3", Kind = DestinationKind.S3Compatible }, vault));
        Assert.Contains("connect your Google Drive account",
            DestinationReadiness.Problem(new Destination { Name = "G", Kind = DestinationKind.GoogleDrive }, vault));
    }

    [Fact]
    public async Task A_run_to_an_unfinished_destination_stops_at_once_with_the_reason()
    {
        if (!ServiceLocator.Initialized) ServiceLocator.InitializeCore();
        using var ws = new TempWorkspace();
        var source = ws.Sub("source");
        File.WriteAllText(Path.Combine(source, "a.txt"), "x");
        var imported = new Destination { Name = "Dropbox - imported", Kind = DestinationKind.DropboxAppScoped, Encrypted = true, PathOrSubpath = "imported" };
        var backup = new Backup { Name = "local_c", SourcePaths = { source }, UseVss = false, VssDisabledAcknowledged = true };
        backup.Targets.Add(new BackupTarget { DestinationId = imported.Id, StorageName = "default" });
        ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Destinations.Clear(); cfg.Destinations.Add(imported); cfg.Backups.Add(backup); });
        try
        {
            var record = await ServiceLocator.Orchestrator.RunBackupAsync(backup, CancellationToken.None);
            Assert.Equal(BackupRunStatus.Failed, record.Status);
            var cell = Assert.Single(record.Cells);
            Assert.Contains("isn't set up yet: connect your Dropbox account and choose a storage password", cell.LastError);
            Assert.Equal(0, cell.AttemptsMade); // no restic attempt, no retries
        }
        finally { ServiceLocator.Config.Update(cfg => { cfg.Backups.Clear(); cfg.Destinations.Clear(); }); }
    }
}
