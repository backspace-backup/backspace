using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;
using Serilog;

namespace Backspace.Services;

/// <summary>
/// Spawns an elevated child process of the running Backspace binary to
/// perform a single VSS-enabled backup, then returns the child's
/// outcome. The main GUI stays unelevated; only the per-run helper
/// crosses the UAC line, and only for the lifetime of one cell.
///
/// Design choices:
///   * <em>One UAC prompt per cell.</em> UAC's consent persistence is
///     per-process, so batching cells would couple the helper's
///     lifetime to the orchestrator's retry cascade. A fresh helper
///     per cell makes consent and cancellation explicit. Scheduled
///     runs already use an elevated task and do not prompt.
///   * <em>Argv + temp-file IPC.</em> The helper takes its
///     instructions on argv (small, well-known fields: backup id,
///     source path, destination id, storage name) and writes its
///     RunRecord result to a caller-provided temp file. argv is
///     bounded by Windows' command-line limit so we can't send the
///     full Backup / Destination objects that way — instead the
///     helper re-reads them from the same ConfigStore the parent
///     just wrote.
///   * <em>Same engine path.</em> The helper boots
///     <see cref="ServiceLocator"/>, picks the same active
///     <see cref="IBackupEngine"/> the main process picked, and routes
///     the cell call through it. The VSS magic comes from restic's
///     <c>--use-fs-snapshot</c> flag (added by
///     <see cref="ResticBackrestEngine.BuildBackupFlags"/>); the
///     helper just provides the Administrator token restic needs to
///     call into the Volume Shadow Copy Service.
/// </summary>
public sealed class VssElevationCoordinator
{
    /// <summary>Argv flag that switches the running process into
    /// helper-mode. Program.cs branches on this before any UI
    /// initialisation so the helper never tries to paint a window.</summary>
    public const string HelperCliArgument = "--vss-elevated-helper";

    /// <summary>Argv flag holding the path to the result file the
    /// helper writes a JSON RunRecord to on exit.</summary>
    public const string ResultFileArgument = "--result-file";
    public const string ConfigRootArgument = "--config-root";

    /// <summary>Argv flag carrying the parent process id. The helper
    /// uses this to watchdog the parent: if Backspace.exe dies (for
    /// any reason — crash, force-kill, user picked Quit from the
    /// tray mid-backup) the helper notices on its next 1 s poll and
    /// terminates instead of finishing the backup as an orphan.
    /// UAC elevation puts the helper outside the parent's job
    /// object so the job-on-close mechanism can't reach it; this
    /// argv-passed pid is how we close that hole.</summary>
    public const string ParentPidArgument = "--parent-pid";

    /// <summary>Argv flag carrying the parent process's StartTime
    /// as round-tripped ticks (UTC). The watchdog uses this to
    /// defend against PID reuse: between two 1 Hz watchdog ticks,
    /// the OS can recycle a dead PID to an unrelated process. By
    /// pinning the parent's StartTime we can detect that the
    /// "live" process at our parent's PID is actually a different
    /// process and bail. Without this, a long-running VSS helper
    /// could survive a parent crash + replacement and continue
    /// writing to the user's repo under an Admin token.</summary>
    public const string ParentStartTimeArgument = "--parent-start-time";

    /// <summary>How long the parent waits for the helper to finish.
    /// Long enough to cover a realistic backup (tens of minutes for
    /// most users) while still bounded so a wedged helper doesn't
    /// hang the GUI forever. The user's <c>CancellationToken</c>
    /// still terminates the child early if they hit Stop.</summary>
    public static readonly TimeSpan DefaultHelperTimeout = TimeSpan.FromHours(6);

    private static ILogger _log => AppLogger.For<VssElevationCoordinator>();

    /// <summary>Whether the elevation flow is wired end-to-end. True
    /// since the helper-mode IPC landed; the orchestrator checks this
    /// before deciding to route a VSS-needing cell through here.</summary>
    public bool IsFullyImplemented => true;

    public async Task<RunRecord> RunElevatedAsync(VssElevationRequest request, CancellationToken ct)
    {
        if (!Services.Platform.PlatformInfo.IsWindows)
            throw new PlatformNotSupportedException("VSS auto-elevate is Windows-only.");

        var exePath = ResolveSelfBinaryPath();
        var resultPath = Path.Combine(Path.GetTempPath(), $"backspace-vss-result-{Guid.NewGuid():N}.json");

        var psi = BuildElevatedStartInfo(exePath, request, resultPath);
        _log.Information(
            "Launching VSS helper for backup={Backup} source={Source} destination={Dest}",
            request.Backup.Name, request.SourcePath, request.Destination.Name);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for the VSS helper.");
            return await WaitForHelperResultAsync(proc, resultPath, request, ct).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user clicked No / Cancel in UAC. Map
            // to a Skipped outcome so the orchestrator surfaces a
            // user-cancellable rather than a hard failure.
            _log.Information("User declined UAC consent for backup={Backup}", request.Backup.Name);
            return MakeSkipped(request, "Skipped: the Windows approval prompt was declined.");
        }
        finally
        {
            // Leave a cancellation marker for a helper which has not yet
            // acknowledged Stop. It may still be waiting for startup IO.
            if (proc is null || proc.HasExited)
            {
                try { File.Delete(resultPath); } catch { }
                try { File.Delete(resultPath + ".cancel"); } catch { }
            }
            proc?.Dispose();
        }
    }

    /// <summary>Read the request out of <paramref name="args"/> the
    /// helper-mode invocation received. Returns null if the required
    /// arguments aren't present — Program.cs treats that as a usage
    /// error and exits with code 2.</summary>
    public static VssHelperArgs? ParseHelperArgs(string[] args)
    {
        string? backupId = null, sourcePath = null, destId = null, storageName = null, resultFile = null, configRoot = null;
        int? parentPid = null;
        long? parentStartTimeTicks = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--backup-id":      backupId    = args[++i]; break;
                case "--source-path":    sourcePath  = args[++i]; break;
                case "--destination-id": destId      = args[++i]; break;
                case "--storage-name":   storageName = args[++i]; break;
                case ResultFileArgument: resultFile  = args[++i]; break;
                case ConfigRootArgument: configRoot = args[++i]; break;
                case ParentPidArgument:
                    if (int.TryParse(args[++i], out var pid)) parentPid = pid;
                    break;
                case ParentStartTimeArgument:
                    if (long.TryParse(args[++i], out var ticks)) parentStartTimeTicks = ticks;
                    break;
            }
        }
        if (backupId is null || sourcePath is null || destId is null || storageName is null || resultFile is null)
            return null;
        return new VssHelperArgs(backupId, sourcePath, destId, storageName, resultFile, parentPid, parentStartTimeTicks, configRoot);
    }

    /// <summary>Helper-mode body: resolves the request against
    /// <see cref="ServiceLocator"/>, runs the per-cell backup through
    /// the active engine, and writes the resulting RunRecord (or a
    /// synthesised failure record on early-out) to
    /// <see cref="VssHelperArgs.ResultFile"/>. Returns the exit code
    /// Program.cs returns to the parent process. Idempotent on the
    /// result-file front: a partial write is overwritten by the next
    /// successful invocation.</summary>
    public static async Task<int> RunHelperAsync(VssHelperArgs args, CancellationToken ct)
    {
        if (!Services.Platform.PlatformInfo.IsWindows || !Services.Platform.PlatformInfo.IsProcessElevated)
        {
            await WriteResultAsync(args.ResultFile, MakeStub(args, BackupRunStatus.Failed,
                "Windows administrator permission is required for a snapshot backup.")).ConfigureAwait(false);
            return 2;
        }
        if (args.ConfigRoot is not null)
            Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", Path.GetFullPath(args.ConfigRoot));
        // Elevating the RPC client does not elevate a daemon it connects to.
        // This helper must launch and own its own elevated daemon/restic tree.
        ServiceLocator.InitializeCore(BackrestSidecar.CreateIsolatedDataDirectory("vss"));

        var cfg = ServiceLocator.Config.Current;
        var backup = cfg.Backups.FirstOrDefault(b => b.Id == args.BackupId);
        var destination = cfg.Destinations.FirstOrDefault(d => d.Id == args.DestinationId);
        if (backup is null || destination is null)
        {
            var msg = backup is null
                ? $"Backup id {args.BackupId} not found in config."
                : $"Destination id {args.DestinationId} not found in config.";
            await WriteResultAsync(args.ResultFile, MakeStub(args, BackupRunStatus.Failed, msg)).ConfigureAwait(false);
            return 2;
        }

        // Parent watchdog. UAC elevation puts us outside the parent's
        // KILL_ON_JOB_CLOSE job, so we have to police the parent's
        // life ourselves. Poll the parent pid every second; if it
        // disappears we fire our own cancellation, which cascades into
        // the engine call and surfaces as a Skipped RunRecord with a
        // breadcrumb in the Summary. Without this, a parent crashed
        // mid-VSS-backup left the elevated helper finishing on its own
        // — minutes of unsupervised disk + cloud activity by an
        // Admin-token process with no remaining UI.
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? watchdogTask = null;
        if (args.ParentPid is int parentPid && parentPid > 0)
        {
            // The expected StartTime, if the parent sent one. Lets
            // the watchdog detect PID reuse: if the live process at
            // parentPid was started AFTER our parent, the OS has
            // recycled the PID to an unrelated process and we should
            // bail. Without this check a long-running helper could
            // be tricked into trusting a different process that
            // happens to have inherited the dead parent's PID.
            DateTime? expectedStartTimeUtc = null;
            if (args.ParentStartTimeTicks is long ticks)
            {
                try { expectedStartTimeUtc = new DateTime(ticks, DateTimeKind.Utc); }
                catch { /* malformed value — skip the StartTime check */ }
            }

            watchdogTask = Task.Run(async () =>
            {
                try
                {
                    while (!watchdogCts.IsCancellationRequested)
                    {
                        if (File.Exists(args.ResultFile + ".cancel"))
                        {
                            watchdogCts.Cancel();
                            return;
                        }
                        try
                        {
                            using var parent = Process.GetProcessById(parentPid);
                            if (parent.HasExited)
                            {
                                watchdogCts.Cancel();
                                return;
                            }
                            if (expectedStartTimeUtc is DateTime expected)
                            {
                                // Exact kernel creation time distinguishes PID reuse.
                                try
                                {
                                    var liveStart = parent.StartTime.ToUniversalTime();
                                    if (liveStart.Ticks != expected.Ticks)
                                    {
                                        watchdogCts.Cancel();
                                        return;
                                    }
                                }
                                catch { /* StartTime not available on this peer */ }
                            }
                        }
                        catch (ArgumentException)
                        {
                            // ArgumentException = pid no longer maps to
                            // a live process. Parent is gone — pull the
                            // plug on our own work.
                            watchdogCts.Cancel();
                            return;
                        }
                        catch
                        {
                            // Access-denied or transient OS error reading
                            // the parent: don't assume the parent is
                            // dead. Try again next tick.
                        }
                        try { await Task.Delay(TimeSpan.FromSeconds(1), watchdogCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                catch { /* never let the watchdog kill the helper directly — only via cancellation */ }
            }, watchdogCts.Token);
        }

        IDisposable? operationLease = null;
        try
        {
            // The parent may exit or finish its cancellation wait before this
            // elevated process acknowledges Stop. Keep our own repository lease
            // until the dedicated daemon is actually gone.
            operationLease = await ServiceLocator.Maintenance.AcquireReadAsync(
                watchdogCts.Token, watchdogCts.Cancel).ConfigureAwait(false);
            var record = await ServiceLocator.Engine.BackupPruneCheckAsync(
                backup, args.SourcePath, destination, args.StorageName, watchdogCts.Token).ConfigureAwait(false);
            await WriteResultAsync(args.ResultFile, record).ConfigureAwait(false);
            return record.Status switch
            {
                BackupRunStatus.Success or BackupRunStatus.Warning => 0,
                BackupRunStatus.Skipped => 4,
                _ => 1,
            };
        }
        catch (OperationCanceledException)
        {
            // Distinguish "parent died" from "caller cancelled" so the
            // result file's Summary is useful for diagnosing the case.
            var parentGone = args.ParentPid is int p && p > 0 && watchdogCts.IsCancellationRequested
                && !ct.IsCancellationRequested && !File.Exists(args.ResultFile + ".cancel");
            var summary = parentGone
                ? "Parent Backspace process exited; VSS helper terminated to avoid running as an orphan."
                : "Helper cancelled.";
            await WriteResultAsync(args.ResultFile, MakeStub(args, BackupRunStatus.Skipped, summary, backup.Name)).ConfigureAwait(false);
            return 3;
        }
        catch (Exception ex)
        {
            await WriteResultAsync(args.ResultFile, MakeStub(args, BackupRunStatus.Failed, ex.Message, backup.Name)).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            watchdogCts.Cancel();
            if (watchdogTask is not null)
            {
                try { await watchdogTask.ConfigureAwait(false); } catch { /* expected on cancel */ }
            }
            await ServiceLocator.BackrestSidecar.DisposeAsync().ConfigureAwait(false);
            operationLease?.Dispose();
        }
    }

    /// <summary>Build the argv the elevated child needs. The resulting
    /// <see cref="ProcessStartInfo"/> carries <c>Verb = "runas"</c>
    /// which is the Windows hook that triggers the UAC consent
    /// dialog.</summary>
    internal static ProcessStartInfo BuildElevatedStartInfo(string exePath, VssElevationRequest request, string? resultFilePath = null)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Cannot launch VSS helper: parent binary not found.", exePath);

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true, // required for Verb = "runas"
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(HelperCliArgument);
        psi.ArgumentList.Add(ConfigRootArgument);
        psi.ArgumentList.Add(Path.GetFullPath(AppPaths.ConfigRoot));
        psi.ArgumentList.Add("--backup-id");
        psi.ArgumentList.Add(request.Backup.Id);
        psi.ArgumentList.Add("--source-path");
        psi.ArgumentList.Add(request.SourcePath);
        psi.ArgumentList.Add("--destination-id");
        psi.ArgumentList.Add(request.Destination.Id);
        psi.ArgumentList.Add("--storage-name");
        psi.ArgumentList.Add(request.StorageName);
        if (resultFilePath is not null)
        {
            psi.ArgumentList.Add(ResultFileArgument);
            psi.ArgumentList.Add(resultFilePath);
        }
        // Pass the parent pid + its StartTime so the helper can
        // watchdog us. UAC elevation puts the helper in a different
        // security boundary, so the KILL_ON_JOB_CLOSE machinery the
        // rest of the engine children rely on doesn't reach it; the
        // helper-side watchdog (see RunHelperAsync) closes that gap.
        // The StartTime defends against PID reuse: if Backspace
        // dies, Windows may recycle its PID to an unrelated process,
        // and we don't want the helper to think the reused PID's
        // owner is its parent.
        using var self = Process.GetCurrentProcess();
        psi.ArgumentList.Add(ParentPidArgument);
        psi.ArgumentList.Add(self.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            psi.ArgumentList.Add(ParentStartTimeArgument);
            psi.ArgumentList.Add(self.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { /* StartTime may be unavailable on some sandboxes — skip the StartTime arg, watchdog falls back to PID-only */ }
        return psi;
    }

    private async Task<RunRecord> WaitForHelperResultAsync(
        Process proc, string resultPath, VssElevationRequest request, CancellationToken ct)
    {
        // The unelevated parent cannot reliably terminate an elevated process.
        // Signal cancellation over the same private per-run IPC directory; the
        // helper cancels the engine and disposes its own elevated daemon tree.
        void RequestStop()
        {
            try { File.WriteAllText(resultPath + ".cancel", "stop"); }
            catch (Exception ex) { _log.Warning(ex, "Could not signal the VSS helper to stop"); }
        }
        using var stopOnCancel = ct.Register(RequestStop);
        using var timeout = new CancellationTokenSource(DefaultHelperTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            RequestStop();
            using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await proc.WaitForExitAsync(acknowledgement.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                _log.Warning("VSS helper has not yet acknowledged cancellation; its watchdog will keep observing the stop marker");
            }
            ct.ThrowIfCancellationRequested();
            return MakeFailed(request,
                $"Snapshot backup exceeded the {DefaultHelperTimeout.TotalHours:F1}h limit. A stop was requested.");
        }

        if (!File.Exists(resultPath))
        {
            // Helper exited without writing the result file -- usually
            // means a crash before ServiceLocator.InitializeCore. Surface
            // the exit code so the user sees something actionable.
            return MakeFailed(request,
                $"VSS helper exited with code {proc.ExitCode} without writing a result record.");
        }
        var json = await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false);
        try
        {
            var record = JsonSerializer.Deserialize<RunRecord>(json, JsonOptions);
            return record ?? MakeFailed(request, "Helper wrote an empty result file.");
        }
        catch (JsonException ex)
        {
            // The helper got far enough to write a result file but the
            // contents aren't valid JSON. Could be a corrupt disk, a
            // partial write before kill -9, or (in pathological cases)
            // a file written by something other than our helper. Map
            // to a Failed RunRecord with the parse error in the
            // Summary so the user has something actionable.
            return MakeFailed(request,
                $"VSS helper wrote an unparseable result file: {ex.Message}");
        }
    }

    private static async Task WriteResultAsync(string path, RunRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
    }

    private static RunRecord MakeSkipped(VssElevationRequest req, string reason) => new()
    {
        BackupId = req.Backup.Id,
        BackupName = req.Backup.Name,
        Kind = RunKind.Backup,
        StartedUtc = DateTime.UtcNow,
        EndedUtc = DateTime.UtcNow,
        Status = BackupRunStatus.Skipped,
        Summary = reason,
    };

    private static RunRecord MakeFailed(VssElevationRequest req, string reason) => new()
    {
        BackupId = req.Backup.Id,
        BackupName = req.Backup.Name,
        Kind = RunKind.Backup,
        StartedUtc = DateTime.UtcNow,
        EndedUtc = DateTime.UtcNow,
        Status = BackupRunStatus.Failed,
        Summary = reason,
        Errors = { reason },
    };

    /// <summary>Synthesize a <see cref="RunRecord"/> for an early-out
    /// path inside the helper (config-lookup miss, OCE, unhandled
    /// exception before the engine could produce its own record).
    /// <paramref name="backupName"/> falls back to the BackupId when
    /// the helper couldn't resolve the backup at all — better to have
    /// the id appear in the LogsView history than a blank row.</summary>
    private static RunRecord MakeStub(VssHelperArgs args, BackupRunStatus status, string summary, string? backupName = null)
    {
        var rec = new RunRecord
        {
            BackupId = args.BackupId,
            BackupName = backupName ?? args.BackupId,
            Kind = RunKind.Backup,
            StartedUtc = DateTime.UtcNow,
            EndedUtc = DateTime.UtcNow,
            Status = status,
            Summary = summary,
        };
        if (status == BackupRunStatus.Failed) rec.Errors.Add(summary);
        return rec;
    }

    private static string ResolveSelfBinaryPath()
    {
        // Process.GetCurrentProcess().MainModule.FileName is what the
        // OS launched us as; using a relative path or Assembly.Location
        // would point at the .dll under apphost layouts.
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new FileNotFoundException("Cannot resolve the current Backspace binary path.", path ?? "(null)");
        return path;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        // Enum-as-string for the cross-process RunRecord IPC: the
        // helper binary and the parent process could otherwise drift
        // on enum ordering and silently flip Success ↔ Failed when
        // an older bundled helper is launched against newer parent
        // code (or vice versa). Reading still accepts integer-encoded
        // legacy payloads.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>Inputs to a single elevated VSS-backup helper invocation.
/// The helper resolves the backup + destination by id against the
/// shared <see cref="ConfigStore"/> the parent process wrote, so we
/// don't have to serialise the full objects over argv.</summary>
public sealed record VssElevationRequest(
    Backup Backup,
    string SourcePath,
    Destination Destination,
    string StorageName);

/// <summary>Parsed argv the helper-mode child reads. The result file
/// is where the helper writes its RunRecord on exit; the parent
/// reads it back.</summary>
public sealed record VssHelperArgs(
    string BackupId,
    string SourcePath,
    string DestinationId,
    string StorageName,
    string ResultFile,
    int? ParentPid = null,
    long? ParentStartTimeTicks = null,
    string? ConfigRoot = null);
