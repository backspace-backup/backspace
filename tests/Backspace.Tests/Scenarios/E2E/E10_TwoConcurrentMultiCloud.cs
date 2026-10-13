using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Real-user E2E: the user's "two parallel backups with cloud
/// destinations" scenario.
///   Backup A → local + Dropbox
///   Backup B → local + Google Drive
///
/// The orchestrator must run both concurrently (disjoint
/// destinations) and produce one revision in each storage.
///
/// Soft-skipping rules:
///   • Both cloud tokens missing → skip entirely.
///   • Only one token missing → run the half that's wired up plus
///     both local-half cells; record the missing-cloud as a note.
///   • Token present but auth fails at runtime → skip with the
///     refresh-token hint (same shape as CloudRoundTripFlow).
/// </summary>
[Collection(ScenarioCollection.Name)]
public sealed class E10_TwoConcurrentMultiCloud : BackupE2EBase
{
    public E10_TwoConcurrentMultiCloud(ITestOutputHelper output) : base(output) { }

    protected override string ScenarioName => "E10-two-concurrent-multi-cloud";

    [AvaloniaFact] public async Task Run_E10() => await Run();

    protected override async Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture)
    {
        var dropbox = await ResolveAsync(CloudCredentialsLoader.Provider.Dropbox);
        var gdrive  = await ResolveAsync(CloudCredentialsLoader.Provider.GoogleDrive);

        if (dropbox is null && gdrive is null)
        {
            ctx.Skip("Skipped: neither DROPBOX_TEST_TOKEN nor GOOGLEDRIVE_TEST_TOKEN " +
                     "present in secrets.env — scenario needs at least one cloud half " +
                     "to be informative.");
            return;
        }

        var sourceA = ctx.MakeSource("project-A", seed: 101, targetBytes: 1_500_000);
        var sourceB = ctx.MakeSource("project-B", seed: 202, targetBytes: 1_500_000);
        var localA  = ctx.MakeLocalDestination("local-A");
        var localB  = ctx.MakeLocalDestination("local-B");

        var destLocalA = new Destination
        {
            Name = "Local A", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = localA, Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-multi-A-local", $"e2e-{Guid.NewGuid():N}"),
        };
        var destLocalB = new Destination
        {
            Name = "Local B", Kind = DestinationKind.LocalFolder,
            PathOrSubpath = localB, Encrypted = true,
            StoragePasswordRef = ServiceLocator.Secrets.PutNew(
                "e2e-multi-B-local", $"e2e-{Guid.NewGuid():N}"),
        };

        var backupA = new Backup
        {
            Name = "Project A (E10)",
            SourcePaths = { sourceA },
            UseVss = false, VssDisabledAcknowledged = true, Threads = 2,
            Targets =
            {
                new BackupTarget { DestinationId = destLocalA.Id, StorageName = "default" },
            },
        };
        var backupB = new Backup
        {
            Name = "Project B (E10)",
            SourcePaths = { sourceB },
            UseVss = false, VssDisabledAcknowledged = true, Threads = 2,
            Targets =
            {
                new BackupTarget { DestinationId = destLocalB.Id, StorageName = "default" },
            },
        };

        Destination? destDropbox = null;
        if (dropbox is not null)
        {
            destDropbox = CloudCredentialsLoader.BuildDestination(
                CloudCredentialsLoader.Provider.Dropbox, dropbox, ServiceLocator.Secrets);
            backupA.Targets.Add(new BackupTarget { DestinationId = destDropbox.Id, StorageName = "default" });
        }
        Destination? destGdrive = null;
        if (gdrive is not null)
        {
            destGdrive = CloudCredentialsLoader.BuildDestination(
                CloudCredentialsLoader.Provider.GoogleDrive, gdrive, ServiceLocator.Secrets);
            backupB.Targets.Add(new BackupTarget { DestinationId = destGdrive.Id, StorageName = "default" });
        }

        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(destLocalA);
            cfg.Destinations.Add(destLocalB);
            if (destDropbox is not null) cfg.Destinations.Add(destDropbox);
            if (destGdrive  is not null) cfg.Destinations.Add(destGdrive);
            cfg.Backups.Add(backupA);
            cfg.Backups.Add(backupB);
        });

        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, "both backups configured",
            $"Backup A targets: local{(destDropbox is null ? "" : " + Dropbox")}. " +
            $"Backup B targets: local{(destGdrive  is null ? "" : " + Google Drive")}.");

        // Both runs concurrently. Each backup's targets fan out
        // sequentially internally (per-storage chain isolation), but
        // the orchestrator parallelises ACROSS backups.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var taskA = Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backupA, CancellationToken.None));
        var taskB = Task.Run(() => ServiceLocator.Orchestrator.RunBackupAsync(backupB, CancellationToken.None));
        var both = await Task.WhenAll(taskA, taskB);
        sw.Stop();

        // Tolerate a soft-fail on a cloud half if its OAuth token
        // looks dead; the local half should always succeed.
        AssertOkOrCloudAuthFail(ctx, "Backup A", both[0]);
        AssertOkOrCloudAuthFail(ctx, "Backup B", both[1]);
        ctx.Snapshot(host, "both backups complete",
            $"Wall-clock {sw.Elapsed.TotalSeconds:F1}s. " +
            $"A={both[0].Status} ({both[0].Summary}); B={both[1].Status} ({both[1].Summary}).");

        // Restore the LOCAL halves and byte-compare. The cloud halves
        // are already verified by E07/E09 individually; we don't need
        // to re-restore them here.
        var restoredA = ctx.MakeRestoreTarget("restored-A");
        var restoredB = ctx.MakeRestoreTarget("restored-B");
        var outcomeA = await RestoreEverythingAsync(
            backupA, sourceA, destLocalA, "default", restoredA, CancellationToken.None);
        var outcomeB = await RestoreEverythingAsync(
            backupB, sourceB, destLocalB, "default", restoredB, CancellationToken.None);
        Assert.Equal(0, outcomeA.FailedCount);
        Assert.Equal(0, outcomeB.FailedCount);
        AssertBytesEqual(sourceA, restoredA, ctx);
        AssertBytesEqual(sourceB, restoredB, ctx);
    }

    /// <summary>Resolve a provider's credential, kicking the
    /// interactive acquirer when the saved token is missing or the
    /// developer's AlwaysShowOAuthUx flag is set. Returns null if
    /// the host can't acquire (no console, opt-out, etc.) so the
    /// scenario can fall back to "one half only".</summary>
    private async Task<CloudCredentialsLoader.Credential?> ResolveAsync(
        CloudCredentialsLoader.Provider provider)
    {
        var existing = CloudCredentialsLoader.TryGet(provider);
        var shouldAcquire = (existing is null || E2EConfig.AlwaysShowOAuthUx)
                            && InteractiveTokenAcquirer.CanAcquireInteractively;
        if (!shouldAcquire) return existing;
        try
        {
            return await InteractiveTokenAcquirer.AcquireAsync(provider, Output);
        }
        catch (Exception ex)
        {
            Output.WriteLine($"[E10] {provider} acquisition failed: {ex.Message}");
            return existing; // fall back to whatever's already there (may be null)
        }
    }

    /// <summary>If the cloud half died on a dead OAuth token, fold
    /// that into a recorded skip-note rather than failing the whole
    /// scenario. The local half is the part that proves the
    /// orchestrator's parallelism contract; the cloud half's
    /// success here is bonus coverage on top of E07/E08/E09.</summary>
    private static void AssertOkOrCloudAuthFail(ScenarioContext ctx, string label, RunRecord r)
    {
        if (r.Status is BackupRunStatus.Success or BackupRunStatus.Warning) return;
        var msg = string.Join(" | ", r.Errors) + " | " + r.Summary;
        // Match HTTP 401/403 in any context (space, colon, EOL, parens) so
        // a "401 Unauthorized" or "(401:" stamp both trigger the soft-skip.
        // Word-boundary regex over substring search avoids "4010" / "4030"
        // false positives.
        var looksAuth = System.Text.RegularExpressions.Regex.IsMatch(
                            msg, @"\b(401|403)\b")
                        || msg.Contains("unauthor", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("expired", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("invalid token", StringComparison.OrdinalIgnoreCase);
        if (looksAuth)
        {
            ctx.Report.RecordAnomaly($"{label} cloud half failed on auth: {msg} " +
                                     "(refresh the matching token in secrets.env to unblock).");
            return;
        }
        Assert.Fail($"{label} ended {r.Status}: {msg}");
    }
}
