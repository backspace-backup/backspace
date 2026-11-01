using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Shared body for "back up to cloud X, restore, byte-compare"
/// scenarios. One file rather than three copies — every provider's
/// flow is identical at the orchestrator + restore level; only the
/// Destination kind / OAuth token JSON differ, both already
/// abstracted by <see cref="CloudCredentialsLoader.BuildDestination"/>.
///
/// Per-provider scenarios stay one-class-per-test so xUnit gets a
/// distinct row in the test explorer (Dropbox vs OneDrive vs Google
/// Drive failures are easier to triage when they're named in the
/// runner).
/// </summary>
internal static class CloudRoundTripFlow
{
    public static async Task RunAsync(
        ScenarioContext ctx,
        CloudCredentialsLoader.Provider provider,
        Xunit.ITestOutputHelper? log = null)
    {
        var cred = CloudCredentialsLoader.TryGet(provider);

        // When the developer flips E2EConfig.AlwaysShowOAuthUx, we
        // force the acquisition path even if a saved token already
        // exists — useful for specifically exercising the OAuth
        // surface ("does the consent flow still work after this
        // change?") without manually deleting the saved token first.
        var shouldAcquire = (cred is null || E2EConfig.AlwaysShowOAuthUx)
                            && InteractiveTokenAcquirer.CanAcquireInteractively;
        if (shouldAcquire && log is not null)
        {
            try
            {
                cred = await InteractiveTokenAcquirer.AcquireAsync(provider, log, default);
            }
            catch (Exception ex)
            {
                ctx.Skip($"Token acquisition for {provider} failed: {ex.Message}. " +
                          $"Re-run from a terminal to retry the OAuth-helper flow, or " +
                          $"set BACKSPACE_E2E_NONINTERACTIVE=1 to skip cloud scenarios.");
                return;
            }
        }

        if (cred is null)
        {
            // Soft-skip: token missing and we can't acquire one
            // interactively (CI host, dev opted out, or stdin not
            // a terminal). SUMMARY.md surfaces the actionable reason.
            ctx.Skip(CloudCredentialsLoader.SkipReason(provider));
            return;
        }

        var sourcePath = ctx.MakeSource("docs", seed: provider.GetHashCode(), targetBytes: 1_500_000);
        var restoreDir = ctx.MakeRestoreTarget("restored");

        var dest = CloudCredentialsLoader.BuildDestination(provider, cred, ServiceLocator.Secrets);
        var backup = new Backup
        {
            Name = $"E2E {provider}",
            SourcePaths = { sourcePath },
            UseVss = false, VssDisabledAcknowledged = true, Threads = 2,
            Targets = { new BackupTarget { DestinationId = dest.Id, StorageName = "default" } },
        };
        ServiceLocator.Config.Update(cfg =>
        {
            cfg.Destinations.Add(dest);
            cfg.Backups.Add(backup);
        });

        try { await RunConfiguredAsync(ctx, provider, backup, dest, sourcePath, restoreDir); }
        finally { CloudCredentialsLoader.SaveRefreshedToken(cred, dest, ServiceLocator.Secrets); }
    }

    private static async Task RunConfiguredAsync(ScenarioContext ctx, CloudCredentialsLoader.Provider provider,
        Backup backup, Destination dest, string sourcePath, string restoreDir)
    {
        var view = new BackupsView { DataContext = new BackupsViewModel() };
        var host = new Window { Width = 1100, Height = 760, Content = view };
        ctx.Driver.Show(host);
        ctx.Snapshot(host, $"{provider} backup configured");

        RunRecord record = null!;
        try
        {
            record = await BackupE2EBase.RunBackupViaUiAsync(ctx, backup, view);
        }
        catch (Exception ex)
        {
            // OAuth tokens expire. Distinguish "user's token is dead"
            // (expected once a few weeks pass) from "the engine
            // genuinely broke." A dead-token failure becomes a soft
            // skip so the run summary tells the user to refresh,
            // rather than a red Failed row.
            if (LooksLikeAuthFailure(ex.Message))
            {
                ctx.Skip($"OAuth token for {provider} looks expired/invalid: {ex.Message}. " +
                          $"Sign in again: see docs/TESTING.md (live cloud tests).");
                return;
            }
            throw;
        }

        if (record.Status == BackupRunStatus.Failed)
        {
            var msg = string.Join(" | ", record.Errors) + " | " + record.Summary;
            if (LooksLikeAuthFailure(msg))
            {
                ctx.Skip($"OAuth token for {provider} looks expired/invalid: {msg}. " +
                          $"Sign in again: see docs/TESTING.md (live cloud tests).");
                return;
            }
        }
        Assert.True(record.Status is BackupRunStatus.Success or BackupRunStatus.Warning,
            $"{provider} backup ended {record.Status}; errors={string.Join(" | ", record.Errors)}.");
        ctx.Snapshot(host, $"{provider} after run", record.Summary);

        var outcome = await Task.Run(() => RunRestoreAsync(
            backup, sourcePath, dest, "default", restoreDir));
        Assert.Equal(0, outcome.FailedCount);

        // We can't call the protected AssertBytesEqual on
        // BackupE2EBase from a static helper, so inline the hash
        // compare here. Same skip-set as the base helper.
        var skipSegments = new[] { ".snapshots", "keys", "data", "index", "locks" };
        var expectedHash = DirectoryHash.Compute(sourcePath, skipSegments);
        var actualHash   = DirectoryHash.Compute(restoreDir, skipSegments);
        Assert.Equal(expectedHash, actualHash);
        ctx.Report.RecordNote(
            $"{provider} byte-equality OK",
            $"Source and restored trees hash identically ({expectedHash[..12]}…).");
    }

    private static async Task<RestoreOutcome> RunRestoreAsync(
        Backup backup, string sourcePath, Destination dest, string storageName, string restoreDir)
    {
        var revisions = await ServiceLocator.Revisions.ListRevisionsAsync(
            backup, sourcePath, dest, storageName, CancellationToken.None);
        Assert.NotEmpty(revisions);
        var files = await ServiceLocator.Revisions.ListFilesAsync(
            backup, sourcePath, dest, storageName, revisions[0].Number, CancellationToken.None);
        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = sourcePath,
            Destination = dest,
            StorageName = storageName,
            Revision = revisions[0].Number,
            Files = files.Select(f => f.Path).ToList(),
            TargetPath = restoreDir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2, 5 },
        };
        return await ServiceLocator.Restore.RunAsync(request, CancellationToken.None);
    }

    /// <summary>OAuth + rclone surface a few well-known error
    /// fragments when a token is expired or revoked. Anything that
    /// matches → soft-skip (the test environment, not the code, is
    /// at fault). Conservative enough that genuine engine bugs still
    /// surface as failures.</summary>
    private static bool LooksLikeAuthFailure(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lower = text.ToLowerInvariant();
        // Word-boundary on 401/403 so a "401:" or "(401)" both match
        // without false-positives on a four-digit chunk size like 4010.
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(401|403)\b"))
            return true;
        return lower.Contains("unauthorized")
            || lower.Contains("token expired")
            || lower.Contains("invalid_grant")
            || lower.Contains("invalid token")
            || lower.Contains("expired_token")
            || lower.Contains("access denied");
    }
}
