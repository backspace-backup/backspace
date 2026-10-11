using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Coordinates restore requests through the engine's native Backrest or
/// owned bundled restic path:
/// ONE call into the engine per restore request, OVERALL progress
/// reporting, no per-file callbacks, no synthetic retry cascade.
///
/// 2026-06-12 refactor: dropped Phase-2 per-failed-file retry,
/// dropped Files dictionary in RestoreOutcome, dropped FileRestored /
/// FileFailed events. The engine is now a thin pump over
/// <see cref="IBackupEngine.RestoreFilesAsync"/>: forward progress,
/// translate the terminal result into a <see cref="RestoreOutcome"/>,
/// surface to subscribers.
/// </summary>
public sealed class RestoreEngine
{
    private static Serilog.ILogger _log => AppLogger.For<RestoreEngine>();

    private readonly ConfigStore _config;
    private readonly IBackupEngine _engine;
    private readonly SecretsStore _secrets;

    public RestoreEngine(ConfigStore config, IBackupEngine engine, SecretsStore secrets)
    {
        _config = config;
        _engine = engine;
        _secrets = secrets;
    }

    public event Action<string>? Log;
    public event Action<RestoreProgressSnapshot>? Progress;

    // ---- public API ----

    public async Task<RestoreOutcome> RunAsync(RestoreRequest request, CancellationToken ct)
    {
        if ((request.IncludePatterns?.Count ?? 0) == 0 && request.Files.Count == 0)
            throw new ArgumentException("No files or include patterns provided.", nameof(request));

        // Maintenance gate (Erase / Wipe / Prune). Returns immediately
        // when no maintenance is in flight (common case).
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ct = operationCts.Token;
        using var readLease = request.MaintenanceLeaseHeld ? null :
            await ServiceLocator.Maintenance.AcquireReadAsync(ct, operationCts.Cancel);

        using var _syncing = AppStatus.BeginSyncing();

        var includes = request.IncludePatterns is { Count: > 0 }
            ? request.IncludePatterns
            : request.Files;

        _log.Information(
            "Restore start: backup={Backup} source={Source} destination={Dest} revision={Rev} patterns={PatternCount} leaves={LeafCount} target={Target}",
            request.Backup.Name, request.SourcePath, request.Destination.Name, request.Revision,
            includes.Count, request.Files.Count, request.TargetPath);

        System.IO.Directory.CreateDirectory(request.TargetPath);

        var outcome = new RestoreOutcome
        {
            RevisionNumber = request.Revision,
            TargetPath = request.TargetPath,
            TotalFiles = request.Files.Count > 0 ? request.Files.Count : includes.Count,
        };

        Log?.Invoke($"[restore] starting {outcome.TotalFiles} item(s)");

        // Register with the multi-restore registry so the "Restores"
        // left-nav cards reflect the in-flight operation.
        ActiveRestoreSession? session = null;
        try
        {
            if (request.ShowInRegistry)
            {
                session = ServiceLocator.ActiveRestores.Begin(request.Backup, request, operationCts.Cancel);
                if (request.SessionOwner is not null) ServiceLocator.ActiveRestores.Open(session);
            }
        }
        catch (Exception ex) { _log.Debug(ex, "Couldn't register active-restore session — non-fatal"); }

        try
        {
            var result = await _engine.RestoreFilesAsync(
                request.Backup, request.SourcePath, request.Destination, request.StorageName,
                request.Revision, includes, request.TargetPath,
                onProgress: p =>
                {
                    outcome.TotalFiles = request.Files.Count > 0 ? request.Files.Count : p.TotalFiles;
                    outcome.RestoredCount = p.FilesRestored;
                    outcome.TotalBytes = p.TotalBytes;
                    outcome.BytesRestored = p.BytesRestored;
                    Progress?.Invoke(outcome.Snapshot());
                    if (session is not null)
                    {
                        try
                        {
                            ServiceLocator.ActiveRestores.UpdateProgress(
                                session, outcome.TotalFiles, p.FilesRestored, p.TotalBytes, p.BytesRestored);
                        }
                        catch { /* registry update never blocks restore */ }
                    }
                },
                ct,
                expectedLeaves: request.Files.Count > 0 ? request.Files : null);

            outcome.TotalFiles = request.Files.Count > 0 ? request.Files.Count : result.TotalFiles;
            outcome.RestoredCount = result.FilesRestored;
            outcome.FailedCount = Math.Max(0, outcome.TotalFiles - result.FilesRestored);
            outcome.TotalBytes = result.TotalBytes;
            outcome.BytesRestored = result.BytesRestored;
            outcome.TerminalStatus = result.Status;
            outcome.ErrorMessage = result.ErrorMessage;
            Progress?.Invoke(outcome.Snapshot());

            _log.Information(
                "Restore done: backup={Backup} status={Status} restored={Restored}/{Total} cancelled={Cancelled}",
                request.Backup.Name, result.Status, result.FilesRestored, outcome.TotalFiles,
                ct.IsCancellationRequested);
            Log?.Invoke($"[restore] done: status={result.Status} {result.FilesRestored}/{outcome.TotalFiles}");
            if (session is not null)
                try
                {
                    ServiceLocator.ActiveRestores.UpdateProgress(session, outcome.TotalFiles,
                        outcome.RestoredCount, outcome.TotalBytes, outcome.BytesRestored);
                    ServiceLocator.ActiveRestores.End(session, result.Status, result.ErrorMessage);
                }
                catch { /* never block */ }
        }
        catch (OperationCanceledException)
        {
            outcome.TerminalStatus = RestoreTerminalStatus.Cancelled;
            outcome.ErrorMessage = "Restore cancelled.";
            outcome.FailedCount = Math.Max(0, outcome.TotalFiles - outcome.RestoredCount);
            if (session is not null) ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Cancelled, outcome.ErrorMessage);
        }
        catch (Exception ex)
        {
            outcome.TerminalStatus = RestoreTerminalStatus.Error;
            outcome.ErrorMessage = ex.Message;
            outcome.FailedCount = outcome.TotalFiles;
            _log.Warning(ex, "Restore threw: backup={Backup}", request.Backup.Name);
            Log?.Invoke($"[restore] error: {ex.Message}");
            if (session is not null)
                try { ServiceLocator.ActiveRestores.End(session, RestoreTerminalStatus.Error, ex.Message); }
                catch { /* never block */ }
            throw;
        }

        return outcome;
    }
}

// ================= request / result shapes =================

public sealed class RestoreRequest
{
    internal bool MaintenanceLeaseHeld { get; init; }
    public bool ShowInRegistry { get; init; } = true;
    public object? SessionOwner { get; init; }
    public required Backup Backup { get; init; }

    /// <summary>
    /// Which source of the backup this restore is for. Multi-source
    /// backups have independent snapshot chains per source, so the
    /// restore wizard picks a source explicitly when more than one
    /// exists. Must equal one of <see cref="Models.Backup.SourcePaths"/>.
    /// </summary>
    public required string SourcePath { get; init; }

    public required Destination Destination { get; init; }
    public required string StorageName { get; init; }
    public required int Revision { get; init; }

    /// <summary>Forward-slash relative paths the user selected. Drives
    /// the bracket-fallback sweep when the engine's `?`-substitution
    /// over-fetches. Optional for callers that only have patterns
    /// (e.g. headless "restore everything"); the engine accepts an
    /// empty list and falls back to <see cref="IncludePatterns"/>.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>Minimal cover of <c>--include</c> patterns the engine
    /// sends to restic. When the user picks whole folders in the
    /// picker, this collapses to one pattern per folder. Use
    /// <see cref="RestoreSelectionMinimizer.Compute"/> to derive.</summary>
    public IReadOnlyList<string>? IncludePatterns { get; init; }

    /// <summary>Absolute path to the restore target. Created if missing.
    /// If this equals the backup's original source path the OVERWRITE
    /// dialog up-stack confirmed before we got here.</summary>
    public required string TargetPath { get; init; }

    public bool Overwrite { get; init; } = true;
    public bool PreserveStructure { get; init; } = true;
    public int Threads { get; init; } = 4;

    /// <summary>Legacy field — Phase-2 retry cascade was dropped in the
    /// 2026-06-12 refactor to match Backrest semantics. Kept on the
    /// shape for source-compatibility; the engine ignores it.</summary>
    public IReadOnlyList<int>? RetryDelaysSeconds { get; init; }
}

public sealed class RestoreOutcome
{
    public int RevisionNumber { get; init; }
    public string TargetPath { get; init; } = "";

    /// <summary>Total files the restore expected to touch. Filled from
    /// <see cref="RestoreRequest.Files"/> when present, else from the
    /// pattern count, then upgraded with restic's authoritative number
    /// once the engine returns.</summary>
    public int TotalFiles { get; set; }

    /// <summary>Files restic restored. Comes from
    /// <see cref="RestoreOpResult.FilesRestored"/>.</summary>
    public int RestoredCount { get; set; }

    /// <summary>TotalFiles - RestoredCount, never negative.</summary>
    public int FailedCount { get; set; }

    public long TotalBytes { get; set; }
    public long BytesRestored { get; set; }

    public RestoreTerminalStatus TerminalStatus { get; set; } = RestoreTerminalStatus.Success;
    public string? ErrorMessage { get; set; }

    public RestoreProgressSnapshot Snapshot() => new(
        TotalFiles, RestoredCount, FailedCount,
        InFlight: 0,
        TotalBytes: TotalBytes,
        BytesRestored: BytesRestored,
        Status: TerminalStatus,
        ErrorMessage: ErrorMessage);
}

public readonly record struct RestoreProgressSnapshot(
    int Total, int Restored, int Failed, int InFlight,
    long TotalBytes, long BytesRestored,
    RestoreTerminalStatus Status, string? ErrorMessage);
