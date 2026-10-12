using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public class StorageCleanerEraseReRunTests
{
    [Fact]
    public async Task ListingFailure_IsNotReportedAsAnEmptyDestination()
    {
        using var tmp = new TempWorkspace("cleanup-failure");
        var engine = DispatchProxy.Create<IBackupEngine, FailingEngine>();
        var cleaner = new StorageCleaner(new ConfigStore(), new SecretsStore(), engine);
        var backup = new Backup { Name = "Important", SourcePaths = { tmp.Sub("source") } };
        var report = await cleaner.EraseBackupFromDestinationAsync(backup,
            new Destination { Name="Offline", Kind=DestinationKind.DropboxAppScoped }, "default", CancellationToken.None);
        Assert.Contains(report.Errors, e => e.Contains("Listing failed"));
        Assert.Empty(report.DeletedSnapshotIds);
        Assert.DoesNotContain(report.Notes, n => n.Contains("nothing to remove"));
        Assert.False(((FailingEngine)(object)engine).PruneCalled);
    }

    public class FailingEngine : DispatchProxy
    {
        public bool PruneCalled;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
            if (method.Name == "EnsureInitializedAsync") return Task.CompletedTask;
            if (method.Name == "ListSnapshotsAsync") throw new IOException("Listing failed: account unavailable");
            PruneCalled = true;
            throw new InvalidOperationException("Unexpected engine operation " + method.Name);
        }
    }
}
