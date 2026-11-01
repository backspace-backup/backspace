using System;
using System.Linq;
using System.Security;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceWorkerTests
{
    [Fact]
    public void ApplyingApprovalPreservesNewerOutcomesAndRemovesUnapprovedPlans()
    {
        using var workspace = new TempWorkspace();
        var store = new ConfigStore(); store.Load();
        var finished = DateTime.UtcNow;
        store.Update(c =>
        {
            c.Backups.Add(new() { Id = "kept", Name = "Old", SourcePaths = { @"C:\Approved" },
                LastSuccessUtc = finished, LastRunStatus = BackupRunStatus.Success,
                LastVerifyPass = true, LastVerifySummary = "Recovered successfully", LastVerifyUtc = finished,
                SourceLastSizeBytes = { [@"C:\Approved"] = 123, [@"C:\Removed"] = 456 } });
            c.Backups.Add(new() { Id = "removed", Name = "No longer approved" });
            c.Destinations.Add(new() { Id = "removed-destination" });
        });
        var approved = new AppConfig
        {
            Backups = { new() { Id = "kept", Name = "Approved name", SourcePaths = { @"C:\Approved" },
                LastRunStatus = BackupRunStatus.NeverRun } },
            Destinations = { new() { Id = "approved-destination", PathOrSubpath = @"D:\Approved" } },
            Logging = new() { RetentionDays = 43 },
        };
        store.ApplyServiceApproval(approved);
        var reopened = new ConfigStore(); reopened.Load();
        var backup = Assert.Single(reopened.Current.Backups);
        Assert.Equal("Approved name", backup.Name);
        Assert.Equal(finished, backup.LastSuccessUtc);
        Assert.Equal(BackupRunStatus.Success, backup.LastRunStatus);
        Assert.True(backup.LastVerifyPass);
        Assert.Equal("Recovered successfully", backup.LastVerifySummary);
        Assert.Equal(123, Assert.Single(backup.SourceLastSizeBytes).Value);
        Assert.Equal("approved-destination", Assert.Single(reopened.Current.Destinations).Id);
        Assert.Equal(43, reopened.Current.Logging.RetentionDays);
        Assert.Equal(BackupRunStatus.NeverRun, approved.Backups[0].LastRunStatus);
        approved.Backups[0].Name = "Mutated caller";
        Assert.Equal("Approved name", store.Current.Backups[0].Name);
    }

    private static ServiceRunAuthorization.AuthorizedRun Run(DestinationKind kind, string source = @"C:\Approved")
    {
        var backup = new Backup { Id = "backup", SourcePaths = { source }, Targets = { new() { DestinationId = "dest" } } };
        return new("revision", backup, new() { Backups = { backup }, Destinations = { new() { Id = "dest", Kind = kind, PathOrSubpath = @"D:\Approved" } } }, RunKind.Backup);
    }

    [Fact]
    public void WorkerPinsBothSourceAndLocalRepository()
        => Assert.Equal(new[] { @"C:\Approved", @"D:\Approved" }, ServiceApprovedWorker.LocalPathsFor(Run(DestinationKind.LocalFolder)).ToArray());

    [Theory]
    [InlineData(RunKind.Check)] [InlineData(RunKind.Prune)]
    public void RepositoryMaintenanceDoesNotRequireTheOriginalSource(RunKind kind)
    {
        var run = Run(DestinationKind.LocalFolder, @"\\offline-server\source") with { Kind = kind };
        run.Backup.CheckRestoreFiles = true;
        Assert.Equal(new[] { @"D:\Approved" }, ServiceApprovedWorker.LocalPathsFor(run).ToArray());
    }

    [Fact]
    public void ExplicitOriginalComparisonStillProtectsSourcePaths()
    {
        var run = Run(DestinationKind.LocalFolder) with { Kind = RunKind.Check };
        run.Backup.CheckRestoreFiles = true;
        run.Backup.RestoreCheckOptions = run.Backup.RestoreCheckOptions with { CompareOriginals = true };
        Assert.Equal(new[] { @"C:\Approved", @"D:\Approved" }, ServiceApprovedWorker.LocalPathsFor(run).ToArray());
        run.Backup.CheckRestoreFiles = false;
        Assert.Equal(new[] { @"D:\Approved" }, ServiceApprovedWorker.LocalPathsFor(run).ToArray());
    }

    // External drives run in the service since U061 (ServiceExternalDrivesTests);
    // a network share is still refused.
    [Fact]
    public void NetworkShareAdmissionFailsClosed()
        => Assert.Throws<NotSupportedException>(() => ServiceApprovedWorker.LocalPathsFor(Run(DestinationKind.NetworkShare)).ToArray());

    [Fact]
    public void WorkerDoesNotTreatNetworkSourcesAsLocalPaths()
        => Assert.Throws<SecurityException>(() => ServiceApprovedWorker.LocalPathsFor(Run(DestinationKind.LocalFolder, @"\\server\share")).ToArray());

    [Fact]
    public void OrdinaryProcessCannotClaimTheVssServiceCapability()
        => Assert.False(ServiceApprovedWorker.HasVerifiedVssIdentity);
}
