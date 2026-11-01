using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>Owns test restores independently of backup cards and navigation.</summary>
public sealed class TestRestoreService
{
    private readonly ConfigStore _config;
    private readonly BackupVerifier _verifier;
    private readonly ConcurrentDictionary<string, ActiveRestoreSession> _running = new();
    private readonly ConcurrentDictionary<string, byte> _loaded = new();
    private readonly object _launchGate = new();
    private string Root => Path.Combine(AppPaths.ConfigRoot, "test-restores");
    internal TestRestoreService(ConfigStore config, BackupVerifier verifier) { _config = config; _verifier = verifier; }
    public bool IsRunning(string backupId) => _running.ContainsKey(backupId);
    public ActiveRestoreSession? FindRunning(string backupId) => _running.TryGetValue(backupId, out var session) ? session : null;

    public ActiveRestoreSession Start(Backup backup, TestRestoreOptions options)
    {
        options.Validate();
        if (_running.TryGetValue(backup.Id, out var active)) return active;
        backup = JsonSerializer.Deserialize<Backup>(JsonSerializer.Serialize(backup))!;
        var journal = new TestRestoreJournal { BackupId = backup.Id, BackupName = backup.Name,
            Options = options, RouteFingerprint = TestRestoreContext.Fingerprint(backup, _config) };
        return Launch(backup, journal, resumed: false);
    }

    public async Task<VerifyOutcome> RunScheduledAsync(Backup backup, CancellationToken ct, bool maintenanceLeaseHeld = false)
    {
        ct.ThrowIfCancellationRequested();
        backup = JsonSerializer.Deserialize<Backup>(JsonSerializer.Serialize(backup))!;
        backup.RestoreCheckOptions.Validate();
        var journal = new TestRestoreJournal { BackupId = backup.Id, BackupName = backup.Name,
            Options = backup.RestoreCheckOptions, Scheduled = true, RouteFingerprint = TestRestoreContext.Fingerprint(backup, _config) };
        var session = Launch(backup, journal, resumed: false, ct, maintenanceLeaseHeld);
        await session.Completion.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return session.VerificationOutcome ?? throw new IOException(session.VerificationError ?? "The automatic restore check did not complete.");
    }

    private ActiveRestoreSession Launch(Backup backup, TestRestoreJournal journal, bool resumed,
        CancellationToken externalCancellation = default, bool maintenanceLeaseHeld = false)
    {
        lock (_launchGate)
        {
            if (_running.TryGetValue(backup.Id, out var existing))
            {
                if (journal.Scheduled) throw new IOException("A Test restore is already running for this backup.");
                return existing;
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
            var control = new RestoreControl(paused: journal.State == "paused");
            var session = ServiceLocator.ActiveRestores.BeginTest(backup, journal.Options.Label, journal.StartedUtc, cts.Cancel, control);
            _running[backup.Id] = session;
            _loaded[journal.Id] = 0;
            var root = Root;
            _ = Task.Run(() => AcquireAndRunAsync(backup, journal, resumed, maintenanceLeaseHeld, root, session, control, cts));
            return session;
        }
    }

    private async Task AcquireAndRunAsync(Backup backup, TestRestoreJournal journal, bool resumed,
        bool maintenanceLeaseHeld, string root, ActiveRestoreSession session, RestoreControl control, CancellationTokenSource cts)
    {
        FileStream? owner = null;
        var handedOff = false;
        try
        {
            Directory.CreateDirectory(root);
            var lockPath = Path.Combine(root, TestRestoreContext.Hash(backup.Id) + ".lock");
            FileAccessRetry.Run(() => owner = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
                lockPath, FileWaitReason.TestRestoreOwner, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            var journalPath = Path.Combine(root, journal.Id + ".json");
            // Another process may have completed this saved attempt while we
            // waited. Never overwrite its terminal record with a stale resume.
            if (resumed)
            {
                journal = JsonSerializer.Deserialize<TestRestoreJournal>(await File.ReadAllTextAsync(journalPath, cts.Token))
                    ?? throw new IOException("Empty test restore journal.");
                if (journal.State is not ("running" or "paused"))
                    throw new IOException("This saved Test restore has already ended. Reopen its saved result instead of resuming it.");
            }
            var context = new TestRestoreContext(journal, journalPath, session,
                Path.Combine(root, "history-" + TestRestoreContext.Hash(backup.Id) + ".json"), control, cts.Token)
                { MaintenanceLeaseHeld = maintenanceLeaseHeld };
            control.Changed += context.SavePauseState;
            session.DismissAction = context.Dismiss;
            context.Phase(journal.State == "paused" ? "Paused. Resume continues the saved snapshots; completed batches are kept."
                : resumed ? "Resuming the saved snapshots · completed batches are kept" : "Preparing snapshot file lists…");
            context.PublishProgress();
            handedOff = true;
            await RunAsync(backup, context, session, cts, owner!);
        }
        catch (Exception ex)
        {
            if (handedOff) throw;
            // No journal belongs to us yet: cancellation or acquisition failure
            // must not modify the other owner's progress or history.
            owner?.Dispose(); cts.Dispose();
            session.VerificationError = ex.Message;
            _running.TryRemove(backup.Id, out _);
            ServiceLocator.ActiveRestores.End(session, ex is OperationCanceledException
                ? RestoreTerminalStatus.Cancelled : RestoreTerminalStatus.Error, ex is OperationCanceledException ? null : ex.Message);
        }
    }

    private async Task RunAsync(Backup backup, TestRestoreContext context, ActiveRestoreSession session,
        CancellationTokenSource cts, FileStream owner)
    {
        var terminal = RestoreTerminalStatus.Error;
        string? terminalError = null;
        try
        {
            context.Save();
            var outcome = await _verifier.VerifyAsync(backup, cts.Token, context);
            cts.Token.ThrowIfCancellationRequested();
            context.Control!.Complete(cancelled: false);
            if (!context.Finish(outcome.OverallPass ? "completed" : "failed", outcome.Summary))
                throw new OperationCanceledException(cts.Token);
            try { _verifier.PersistOutcome(outcome); }
            catch (Exception ex) { AppLogger.For<TestRestoreService>().Error(ex, "Could not update the backup card with the saved test restore result"); }
            ServiceLocator.ActiveRestores.Describe(session, outcome.Summary);
            terminal = outcome.OverallPass ? RestoreTerminalStatus.Success : RestoreTerminalStatus.Error;
            terminalError = outcome.OverallPass ? null : outcome.FatalError;
            session.VerificationOutcome = outcome;
            if (!context.Journal.Scheduled) _ = NotifyOutcomeAsync(backup, context.Journal.Options.Label, outcome);
        }
        catch (OperationCanceledException)
        {
            cts.Cancel();
            string? saveFailure = null;
            try
            {
                context.Control!.Complete(cancelled: true);
                context.Finish("cancelled", "Cancelled. Temporary copies were removed. Completed checks remain in the history; this test will not resume.");
            }
            catch (Exception ex)
            {
                AppLogger.For<TestRestoreService>().Error(ex, "Could not save cancelled test restore");
                saveFailure = "Cancellation needs attention. If its saved state could not be updated, this check may resume next time Backspace opens. " + ex.Message;
            }
            finally
            {
                ServiceLocator.ActiveRestores.Describe(session, context.Journal.Summary);
                terminal = saveFailure is null ? RestoreTerminalStatus.Cancelled : RestoreTerminalStatus.Error;
                terminalError = saveFailure;
                PersistInterruptedAttempt(context, BackupRunStatus.Skipped,
                    "Test restore cancelled before completion. Earlier completed results have not been replaced.");
            }
        }
        catch (Exception ex)
        {
            try { context.Control!.Complete(cancelled: true); } catch (Exception cleanup) { AppLogger.For<TestRestoreService>().Error(cleanup, "Test restore cleanup needs attention"); }
            try { context.Finish("failed", ex.Message, allowCancelledFailure: true); } catch (Exception saveError) { AppLogger.For<TestRestoreService>().Error(saveError, "Could not save test restore failure"); }
            terminal = RestoreTerminalStatus.Error; terminalError = ex.Message;
            PersistInterruptedAttempt(context, BackupRunStatus.Failed, "Test restore could not finish: " + ex.Message);
        }
        finally
        {
            session.VerificationError = terminalError;
            if (context.Control is not null) context.Control.Changed -= context.SavePauseState;
            cts.Dispose(); owner.Dispose();
            _running.TryRemove(backup.Id, out _);
            // Completion means the owner lock is released too. A caller that
            // immediately starts another check must not race the old owner.
            ServiceLocator.ActiveRestores.End(session, terminal, terminalError);
        }
    }

    private static void PersistInterruptedAttempt(TestRestoreContext context, BackupRunStatus status, string summary)
    {
        try
        {
            var journal = context.Journal;
            var record = ServiceLocator.Logs.LoadHistoryByBackupId(journal.BackupId).FirstOrDefault(r => r.Id == journal.Id)
                ?? new RunRecord { Id = journal.Id, BackupId = journal.BackupId, BackupName = journal.BackupName,
                    Kind = RunKind.TestRestore, StartedUtc = journal.StartedUtc };
            record.EndedUtc = DateTime.UtcNow;
            record.Status = status;
            record.Summary = summary;
            record.TriggeredAutomatically = journal.Scheduled;
            record.Checks = new() { new BackupCheckResult
            {
                Depth = journal.Options.Full ? CheckDepth.RestoreAllFiles : CheckDepth.RestoreSample,
                Status = status, EndedUtc = record.EndedUtc.Value, Summary = summary,
            } };
            ServiceLocator.Logs.AppendRun(record);
        }
        catch (Exception ex) { AppLogger.For<TestRestoreService>().Warning(ex, "Could not save interrupted restore-check history"); }
    }

    private static async Task NotifyOutcomeAsync(Backup backup, string label, VerifyOutcome outcome)
    {
        try
        {
            await ServiceLocator.Notifier.NotifyAsync(
                title: $"{backup.Name}: {label} {(outcome.OverallPass ? "succeeded" : "FAILED")}",
                detail: outcome.Summary,
                severity: outcome.OverallPass ? NotificationSeverity.Success : NotificationSeverity.Failure,
                dismissAfter: TimeSpan.FromSeconds(13));
        }
        catch (Exception ex) { AppLogger.For<TestRestoreService>().Error(ex, "Could not show test restore notification"); }
    }

    public async Task ResumeInterruptedAsync()
    {
        if (!Directory.Exists(Root)) return;
        string[] paths;
        try { paths = Directory.GetFiles(Root, "*.json"); }
        catch (Exception ex) { RecoveryError(Root, ex); return; }
        foreach (var path in paths)
        {
            if (Path.GetFileName(path).StartsWith("history-", StringComparison.Ordinal)) continue;
            try
            {
                var journal = JsonSerializer.Deserialize<TestRestoreJournal>(await File.ReadAllTextAsync(path))
                    ?? throw new IOException("Empty test restore journal.");
                if (_loaded.ContainsKey(journal.Id)) continue;
                if (!Guid.TryParseExact(journal.Id, "N", out _) || Path.GetFileName(path) != journal.Id + ".json")
                    throw new IOException("Invalid test restore journal identity.");
                journal.Options.Validate();
                if (journal.Dismissed) continue;
                var backup = _config.Current.Backups.FirstOrDefault(b => b.Id == journal.BackupId);
                if (journal.State is not ("running" or "paused"))
                {
                    if (journal.EndedUtc < DateTime.UtcNow.AddDays(-30)) continue;
                    ShowSavedResult(backup, journal, path);
                    continue;
                }
                if (_running.ContainsKey(journal.BackupId)) continue;
                if (backup is null || TestRestoreContext.Fingerprint(backup, _config) != journal.RouteFingerprint)
                {
                    journal.State = "failed"; journal.Summary = "Cannot resume: the backup sources or destination settings changed. Start a new test restore.";
                    journal.EndedUtc = DateTime.UtcNow;
                    await Task.Run(() => TestRestoreContext.WriteAtomic(path, journal, reason: FileWaitReason.Result));
                    ShowSavedResult(backup, journal, path);
                    continue;
                }
                // Run registration on the UI thread, just like an explicit click.
                await Dispatcher.UIThread.InvokeAsync(() => Launch(backup, journal, resumed: true));
            }
            catch (IOException ex) { RecoveryError(path, ex); }
            catch (Exception ex) { RecoveryError(path, ex); }
        }
    }

    private void ShowSavedResult(Backup? backup, TestRestoreJournal journal, string path)
    {
        var saved = ServiceLocator.ActiveRestores.BeginTest(backup ?? new Backup { Id = journal.BackupId, Name = journal.BackupName }, journal.Options.Label, journal.StartedUtc, null);
        ServiceLocator.ActiveRestores.Describe(saved, journal.Summary);
        ServiceLocator.ActiveRestores.UpdateProgress(saved, journal.Cells.Sum(c => c.Files.Count), journal.Cells.Sum(c => c.Results.Count(r => r.Path != "(restore operation)")),
            journal.Cells.Sum(c => c.Files.Sum(f => Math.Max(0, f.SizeBytes))), journal.Cells.Sum(c => c.Results.Sum(r => Math.Max(0, r.ExpectedBytes))));
        ServiceLocator.ActiveRestores.End(saved, journal.State == "completed" ? RestoreTerminalStatus.Success : journal.State == "cancelled" ? RestoreTerminalStatus.Cancelled : RestoreTerminalStatus.Error,
            journal.State == "failed" ? journal.Summary : null);
        saved.DismissAction = () =>
        {
            journal.Dismissed = true;
            try { TestRestoreContext.WriteAtomic(path, journal, reason: FileWaitReason.Dismiss); }
            catch { journal.Dismissed = false; throw; }
        };
        _loaded[journal.Id] = 0;
    }

    private void RecoveryError(string path, Exception error)
    {
        AppLogger.For<TestRestoreService>().Error(error, "Could not resume test restore: {Path}", path);
        if (!_loaded.TryAdd("error:" + path, 0)) return;
        var session = ServiceLocator.ActiveRestores.BeginTest(new Backup { Name = "Interrupted test restore" },
            "Recovery needs attention", DateTime.UtcNow, null);
        ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Error,
            "Saved progress was preserved, but this check could not resume. Another Backspace window may own it, or the saved file may be unreadable. " + error.Message);
    }
}
