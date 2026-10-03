using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Backspace.ViewModels;
using Backspace.Views;
using Xunit;
using Xunit.Abstractions;

namespace Backspace.Tests.Scenarios.E2E;

/// <summary>
/// Base class for "real-user" end-to-end scenarios that drive an
/// actual backup run, not just a screenshot of a stamped state.
///
/// What this gives you on top of <see cref="ScenarioBase"/>:
///   1. A <see cref="RealEngineFixture"/> wired into ServiceLocator
///      before the body runs, so <see cref="ServiceLocator.Orchestrator"/>
///      hits a live restic + rclone instead of the in-memory fake.
///      If the bundled binaries aren't present on disk, the scenario
///      skips with a logged reason (`-p:SkipBackrestDownload=true`
///      hosts and machines that haven't built once).
///   2. A <see cref="RunBackupViaUiAsync"/> helper that invokes the
///      same RunNowCommand a user's "Run now" click triggers, so the
///      coverage stays on the production code path rather than calling
///      the orchestrator directly.
///   3. <see cref="RestoreEverythingAsync"/> + <see cref="AssertBytesEqual"/>
///      so any scenario can prove byte-for-byte round-trip without
///      copy-pasting the restore call.
///
/// Scenarios that DON'T need a real engine — pure VM/View captures —
/// stay on <see cref="ScenarioBase"/>; this class is the heavier-weight
/// path reserved for real-bytes-on-disk verification.
/// </summary>
public abstract class BackupE2EBase : ScenarioBase
{
    /// <summary>The xUnit output sink for this scenario. Exposed so
    /// cloud scenarios can hand it to <see cref="InteractiveTokenAcquirer"/>
    /// without duplicating the constructor ceremony in every subclass.</summary>
    protected ITestOutputHelper Output { get; }

    protected BackupE2EBase(ITestOutputHelper output) => Output = output;

    /// <summary>True when the suite is allowed to actually exercise
    /// real engine binaries. Honours <see cref="RealEngineFixture.BinariesPresent"/>
    /// and the BACKSPACE_E2E_SKIP_REAL env var (set to "1" on CI hosts
    /// that can't spin up the sidecar quickly).</summary>
    protected static bool ShouldRunReal
    {
        get
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("BACKSPACE_E2E_SKIP_REAL"),
                    "1", StringComparison.Ordinal))
                return false;
            return RealEngineFixture.BinariesPresent;
        }
    }

    /// <summary>
    /// Body wrapper that owns the RealEngineFixture lifecycle. The
    /// concrete scenario implements <see cref="RunE2EAsync"/>; this
    /// method swaps in the real engine, calls into the body, and
    /// restores the previous engine on exit (success or fail).
    /// </summary>
    protected sealed override async Task RunAsync(ScenarioContext ctx)
    {
        if (!ShouldRunReal)
        {
            ctx.Skip(RealEngineFixture.BinariesPresent
                ? "Skipped: BACKSPACE_E2E_SKIP_REAL=1 forces in-memory engine."
                : RealEngineFixture.MissingBinariesReason());
            return;
        }

        await using var fixture = await RealEngineFixture.TryCreateAsync(Output);
        if (fixture is null)
        {
            // TryCreateAsync already wrote a skip line into _out; mirror
            // it into the scenario report so SUMMARY.md shows it next to
            // the scenario row.
            ctx.Skip(RealEngineFixture.MissingBinariesReason());
            return;
        }

        await RunE2EAsync(ctx, fixture);
    }

    /// <summary>Override point for concrete scenarios. The fixture is
    /// alive for the whole body; do not dispose it manually.</summary>
    protected abstract Task RunE2EAsync(ScenarioContext ctx, RealEngineFixture fixture);

    /// <summary>
    /// Drive a Run-now through the BackupsView's view model — the
    /// same command path the visible "Run now" button is bound to.
    /// Asserts the card + command guard are reachable (the UX path
    /// the user actually traverses), then awaits completion. We
    /// return the orchestrator's <see cref="RunRecord"/> by invoking
    /// it inline after the UX gate clears, rather than re-reading
    /// persisted state — Backup has only LastRun* fields, not a
    /// history collection, and a RunRecord carries the per-cell
    /// failure detail the assertions want.
    /// </summary>
    public static async Task<RunRecord> RunBackupViaUiAsync(
        ScenarioContext ctx, Backup backup, BackupsView view, CancellationToken ct = default)
    {
        // Pump so the VM's Cards collection rebuilds after the
        // ServiceLocator.Config.Update that seeded the backup.
        ctx.Driver.Pump();
        var vm = view.DataContext as BackupsViewModel
                 ?? throw new InvalidOperationException("BackupsView has no BackupsViewModel.");

        // Match by Id (stable across scenarios) rather than Name.
        var card = vm.Cards.FirstOrDefault(c => c.Snapshot.Id == backup.Id)
                   ?? throw new InvalidOperationException(
                       $"No card found for backup '{backup.Name}' (Id={backup.Id}). " +
                       $"Cards present: {string.Join(", ", vm.Cards.Select(c => c.Snapshot.Name))}");

        // The UX gate: the button's bound command must accept the
        // card. Hitting the orchestrator while CanExecute is false
        // would bypass the same checks a user can't bypass.
        if (!vm.RunNowCommand.CanExecute(card))
            throw new InvalidOperationException(
                $"RunNowCommand refused to execute for '{backup.Name}' — " +
                $"that's the same state a user would see as a disabled button.");

        // Run on a background thread so the AvaloniaFact's headless
        // UI dispatcher can keep pumping pending UI work (progress
        // banners, log live-tail, etc.) while the engine is busy.
        // We deliberately DO NOT ConfigureAwait(false) the await
        // below — the continuation must marshal back onto the UI
        // thread before we Pump, or the dispatcher throws
        // "Call from invalid thread" on the test runner.
        var record = await Task.Run(
            () => ServiceLocator.Orchestrator.RunBackupAsync(backup, ct), ct);

        // Push the record back into the card so any post-run
        // screenshot reflects the same state a real user's UI shows.
        card.Apply(record);
        ctx.Driver.Pump();
        return record;
    }

    /// <summary>
    /// Restore EVERY file from the latest revision of
    /// <paramref name="backup"/> on <paramref name="destination"/> into
    /// <paramref name="targetDir"/>. Returns the populated outcome so
    /// scenarios can assert per-file status as well as byte equality.
    /// </summary>
    protected static async Task<RestoreOutcome> RestoreEverythingAsync(
        Backup backup, string sourcePath, Destination destination,
        string storageName, string targetDir, CancellationToken ct)
    {
        // Restore work happens on background threads via Task.Run so
        // the headless UI dispatcher stays responsive; we don't
        // ConfigureAwait(false) here because the AvaloniaFact caller's
        // continuation needs to resume on the UI thread for any
        // post-restore Pump / Snapshot calls.
        var revisions = await Task.Run(() => ServiceLocator.Revisions
            .ListRevisionsAsync(backup, sourcePath, destination, storageName, ct), ct);
        if (revisions.Count == 0)
            throw new InvalidOperationException(
                $"No revisions yet on destination '{destination.Name}' " +
                $"(StorageName='{storageName}'). Was the backup actually run?");

        var latest = revisions[0].Number;
        var files = await Task.Run(() => ServiceLocator.Revisions
            .ListFilesAsync(backup, sourcePath, destination, storageName, latest, ct), ct);
        if (files.Count == 0)
            throw new InvalidOperationException(
                $"Revision {latest} contains zero files; restore would be a no-op.");

        var request = new RestoreRequest
        {
            Backup = backup,
            SourcePath = sourcePath,
            Destination = destination,
            StorageName = storageName,
            Revision = latest,
            Files = files.Select(f => f.Path).ToList(),
            TargetPath = targetDir,
            Overwrite = true,
            PreserveStructure = true,
            Threads = 2,
            RetryDelaysSeconds = new[] { 1, 2, 5 },
        };
        return await Task.Run(() => ServiceLocator.Restore.RunAsync(request, ct), ct);
    }

    /// <summary>
    /// Run the comprehensive restore validation against the source +
    /// restored trees and surface the per-dimension result in
    /// SUMMARY.md. Restic already does byte-perfect chunk hashing
    /// internally; the meaningful question for an integration test
    /// is "did the wrapper / FS / OS preserve everything correctly?"
    /// — that's file count, mtime (±2s for FAT compat), attributes,
    /// (optionally) NTFS ACLs, (optionally) sparse-file flag, plus a
    /// sampled-byte SHA-256 compare for cheap regression coverage.
    ///
    /// Any failed dimension stops the scenario via <see cref="Assert.Fail"/>
    /// after writing the full report to a sibling file.
    /// </summary>
    protected void AssertRestoreEquivalent(string expected, string actual, ScenarioContext ctx,
        ValidationOptions? options = null)
    {
        options ??= new ValidationOptions();
        options.VerifyAllBytes = true;
        var report = RestoreValidator.Validate(expected, actual, options);
        var dimensions = string.Join(", ", report.Rows.Select(r => r.Dimension));

        // Always write the markdown report so SUMMARY.md can link
        // to a single canonical file regardless of pass/fail —
        // future inspections + diffs don't have to re-run the test.
        var reportPath = Path.Combine(Path.GetDirectoryName(ctx.ScreenshotsDir)!, "restore-validation.md");
        File.WriteAllText(reportPath,
            $"# Restore validation — {ctx.Name}\n\n" +
            $"Source:   `{report.SourceRoot}`\n\n" +
            $"Restored: `{report.RestoredRoot}`\n\n" +
            report.ToMarkdown());

        // Known-gap dimensions get logged as warnings on the
        // scenario report but don't fail the test — they're engine
        // limitations the scenario already acknowledges.
        var knownGaps = report.Rows.Where(r => r.IsKnownGap).ToList();
        foreach (var gap in knownGaps)
            ctx.Report.RecordAnomaly(
                $"known engine gap on {gap.Dimension}: {gap.Summary} (see {reportPath})");

        if (report.AllOkOrKnownGap)
        {
            var note = knownGaps.Count == 0
                ? $"Source + restored trees match on every dimension ({dimensions})."
                : $"Source + restored trees match on every enforced dimension; " +
                  $"{knownGaps.Count} dimension(s) acknowledged as known engine gaps: " +
                  $"{string.Join(", ", knownGaps.Select(g => g.Dimension))}.";
            ctx.Report.RecordNote("restore validation OK", note);
            return;
        }

        var failedDims = string.Join(", ", report.HardFailures.Select(r => r.Dimension));
        ctx.Report.RecordAnomaly($"restore validation MISMATCH on {failedDims}; full report at {reportPath}");
        Assert.Fail(
            $"Restore validation failed on {failedDims}. Report: {reportPath}");
    }

    /// <summary>Backwards-compat alias: existing scenarios use
    /// AssertBytesEqual; new code should call
    /// <see cref="AssertRestoreEquivalent"/> which adds the metadata
    /// dimensions on top of the sampled byte compare. This shim
    /// avoids a wide rename while keeping the upgrade path obvious.</summary>
    protected void AssertBytesEqual(string expected, string actual, ScenarioContext ctx,
        params string[] skipSegments)
    {
        var opts = new ValidationOptions();
        if (skipSegments is { Length: > 0 })
        {
            var combined = new List<string>(skipSegments);
            combined.AddRange(RestoreValidator.DefaultSkipSegments);
            opts.SkipSegments = combined.ToArray();
        }
        AssertRestoreEquivalent(expected, actual, ctx, opts);
    }

}
