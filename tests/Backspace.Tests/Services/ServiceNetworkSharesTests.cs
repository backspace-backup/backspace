using System.Collections.Generic;
using System.IO;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U071: the background service connects to a network share with the user
/// name and password saved for it, before each run, and disconnects after.
/// Windows' own connection call is replaced by a recording fake; the lab VM
/// checks the real one against an SMB share that refuses the service's
/// account without credentials.
/// </summary>
public sealed class ServiceNetworkSharesTests
{
    private sealed class FakeConnector : ServiceNetworkShares.INetworkConnector
    {
        public readonly Queue<int> Results = new();
        public readonly List<string> Calls = new();
        public int Connect(string root, string user, string password)
        {
            Calls.Add($"connect {root} {user} {password}");
            return Results.Count > 0 ? Results.Dequeue() : 0;
        }
        public void Disconnect(string root) => Calls.Add($"disconnect {root}");
    }

    private static (AppConfig Config, Backup Backup, Destination Share, SecretsStore Vault, TempWorkspace Ws) Setup(string path = @"\\nas\backups\pc")
    {
        var ws = new TempWorkspace();
        var vault = new SecretsStore();
        var share = new Destination { Name = "Home NAS", Kind = DestinationKind.NetworkShare, PathOrSubpath = path,
            NetworkUsername = "backup", NetworkPasswordRef = "dest:nas:netpw" };
        vault.Set("dest:nas:netpw", "s3cret");
        var local = new Destination { Name = "Disk", Kind = DestinationKind.LocalFolder, PathOrSubpath = @"D:\Backups" };
        var backup = new Backup { Name = "Photos" };
        backup.Targets.Add(new BackupTarget { DestinationId = share.Id });
        backup.Targets.Add(new BackupTarget { DestinationId = local.Id });
        var config = new AppConfig(); config.Destinations.Add(share); config.Destinations.Add(local); config.Backups.Add(backup);
        return (config, backup, share, vault, ws);
    }

    [Theory]
    [InlineData(@"\\nas\backups\pc\photos", @"\\nas\backups")]
    [InlineData(@"\\nas\backups", @"\\nas\backups")]
    [InlineData(@"\\nas", null)]
    [InlineData(@"Z:\backups", null)]
    [InlineData(@"\\?\C:\backups", null)]
    [InlineData(@"\\.\pipe\x", null)]
    [InlineData("", null)]
    public void The_share_of_a_network_path(string path, string? root)
        => Assert.Equal(root, NetworkSharePath.ShareRoot(path));

    [Fact]
    public void Connects_once_per_share_with_the_saved_credentials_and_disconnects_after()
    {
        var (config, backup, share, vault, ws) = Setup();
        using var _ = ws;
        // A second destination on the same share is connected only once.
        var second = new Destination { Name = "NAS 2", Kind = DestinationKind.NetworkShare, PathOrSubpath = @"\\NAS\Backups\other",
            NetworkUsername = "backup", NetworkPasswordRef = "dest:nas:netpw" };
        config.Destinations.Add(second); backup.Targets.Add(new BackupTarget { DestinationId = second.Id });
        var fake = new FakeConnector();
        using (ServiceNetworkShares.Connect(backup, config, vault, fake))
            Assert.Equal(new[] { @"connect \\nas\backups backup s3cret" }, fake.Calls);
        Assert.Equal(new[] { @"connect \\nas\backups backup s3cret", @"disconnect \\nas\backups" }, fake.Calls);
    }

    [Fact]
    public void A_refused_password_explains_itself_and_leaves_nothing_connected()
    {
        var (config, backup, _, vault, ws) = Setup();
        using var _ = ws;
        var fake = new FakeConnector(); fake.Results.Enqueue(1326);
        var ex = Assert.Throws<ShareConnectionException>(() => ServiceNetworkShares.Connect(backup, config, vault, fake));
        Assert.Contains("«Home NAS»", ex.Message);
        Assert.Contains("refused the user name or password", ex.Message);
        Assert.DoesNotContain("s3cret", ex.Message);
        Assert.Equal(new[] { @"connect \\nas\backups backup s3cret" }, fake.Calls);
    }

    [Fact]
    public void An_unreachable_share_says_to_check_the_device()
    {
        var (config, backup, _, vault, ws) = Setup();
        using var _ = ws;
        var fake = new FakeConnector(); fake.Results.Enqueue(53);
        var ex = Assert.Throws<ShareConnectionException>(() => ServiceNetworkShares.Connect(backup, config, vault, fake));
        Assert.Contains("cannot be reached", ex.Message);
    }

    [Fact]
    public void An_earlier_connection_with_other_credentials_is_replaced()
    {
        var (config, backup, _, vault, ws) = Setup();
        using var _ = ws;
        var fake = new FakeConnector(); fake.Results.Enqueue(ServiceNetworkShares.ErrorSessionCredentialConflict); fake.Results.Enqueue(0);
        using (ServiceNetworkShares.Connect(backup, config, vault, fake)) { }
        Assert.Equal(new[] { @"connect \\nas\backups backup s3cret", @"disconnect \\nas\backups",
            @"connect \\nas\backups backup s3cret", @"disconnect \\nas\backups" }, fake.Calls);
    }

    [Fact]
    public void A_failure_on_the_second_share_disconnects_the_first()
    {
        var (config, backup, _, vault, ws) = Setup();
        using var _ = ws;
        var other = new Destination { Name = "Office", Kind = DestinationKind.NetworkShare, PathOrSubpath = @"\\office\backups",
            NetworkUsername = "backup", NetworkPasswordRef = "dest:nas:netpw" };
        config.Destinations.Add(other); backup.Targets.Add(new BackupTarget { DestinationId = other.Id });
        var fake = new FakeConnector(); fake.Results.Enqueue(0); fake.Results.Enqueue(1326);
        Assert.Throws<ShareConnectionException>(() => ServiceNetworkShares.Connect(backup, config, vault, fake));
        Assert.Equal(@"disconnect \\nas\backups", fake.Calls[^1]);
    }

    [Fact]
    public void A_missing_password_is_refused_before_any_connection()
    {
        var (config, backup, share, vault, ws) = Setup();
        using var _ = ws;
        vault.Remove(share.NetworkPasswordRef!);
        var fake = new FakeConnector();
        var ex = Assert.Throws<ShareConnectionException>(() => ServiceNetworkShares.Connect(backup, config, vault, fake));
        Assert.Equal("The background service signs in to the network share «Home NAS» with its own user name and password, and none are saved. Edit the destination and enter them.", ex.Message);
        Assert.Empty(fake.Calls);
    }

    /// <summary>U171: with the service set up it runs every backup, so a share
    /// saved without its user name (it used to stay with the window) fails the
    /// run with what to enter, before any connection or engine start.</summary>
    [Fact]
    public void A_share_saved_without_a_user_name_fails_saying_what_to_enter()
    {
        var (config, backup, share, vault, ws) = Setup();
        using var _ = ws;
        share.NetworkUsername = null;
        var fake = new FakeConnector();
        var ex = Assert.Throws<ShareConnectionException>(() => ServiceNetworkShares.Connect(backup, config, vault, fake));
        Assert.Contains("«Home NAS»", ex.Message);
        Assert.Contains("enter them", ex.Message);
        Assert.Empty(fake.Calls);
    }

    /// <summary>U171: a drive letter exists only in the owner's sign-in. It
    /// used to be skipped here, leaving the engine to fail on a missing Z:.</summary>
    [Fact]
    public void A_share_saved_as_a_drive_letter_fails_asking_for_its_network_path()
    {
        var (config, backup, _, vault, ws) = Setup(@"Z:\backups\pc");
        using var _ = ws;
        var fake = new FakeConnector();
        var ex = Assert.Throws<ShareConnectionException>(() => ServiceNetworkShares.Connect(backup, config, vault, fake));
        Assert.Contains(@"Z:\backups\pc", ex.Message);
        Assert.Contains(@"enter its network path, like \\server\share\folder", ex.Message);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void A_backup_without_network_shares_connects_nothing()
    {
        var (config, backup, share, vault, ws) = Setup();
        using var _ = ws;
        backup.Targets.RemoveAll(t => t.DestinationId == share.Id);
        var fake = new FakeConnector();
        using (ServiceNetworkShares.Connect(backup, config, vault, fake)) { }
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void A_path_that_is_not_a_mapped_drive_is_kept()
    {
        Assert.Equal(@"\\nas\backups\pc", ServiceNetworkShares.ToNetworkPath(@"\\nas\backups\pc"));
        Assert.Equal(System.Environment.SystemDirectory, ServiceNetworkShares.ToNetworkPath(System.Environment.SystemDirectory));
    }
}
