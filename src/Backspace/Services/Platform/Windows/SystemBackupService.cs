using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// The version-3 background service (U100): Windows' own account, one per
/// Windows account. It runs the owner's saved settings as they are (no
/// separate approval), keeps the schedule, opens the portable vault with its
/// own key copy (U153), and runs each backup in a worker process. Quit stops it;
/// it starts again at the next sign-in, restart or Windows start
/// (<see cref="SystemServiceLifetime"/>).
///
/// <para>Several workers run at once (U156): one per backup, up to
/// <see cref="MaxWorkers"/>. How many actually back up together is decided in
/// the workers by "Backups at once per destination" and "in total"
/// (<see cref="RepositoryAccess"/>); a worker waiting for its turn reports
/// "Queued: …" through its activity. Before, the service ran one worker at a
/// time and a second Run now waited, unreported, while its card said
/// "Running…" (local_c and local_d, 7 November 2026).</para>
/// </summary>
internal sealed class SystemBackupService : ServiceBase
{
    /// <summary>Workers at once; a start beyond it is answered "busy".</summary>
    internal const int MaxWorkers = BackupLimits.Maximum;

    private readonly SystemService.Registration _registration;
    private readonly ServiceWorkerBinding _binding;
    private readonly string _installation;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly ServiceSizeMeasurements _measurements;
    private readonly ServiceRunRecovery _recovery;
    private Task? _lifetime, _leaseMonitor;
    // Each worker's run, and the last finished run of each backup, by run id.
    private readonly Dictionary<string, WorkerRun> _runs = new(StringComparer.Ordinal);
    private WorkerRun? _latest;
    private IReadOnlyList<Destination> _scheduleDestinations = [];
    private string? _returnId;
    private CancellationTokenSource? _returnStop;
    private bool _unreadableSettingsLogged;

    /// <summary>One worker's run, as the activity and the run pipe report it.</summary>
    private sealed class WorkerRun(string runId)
    {
        internal string RunId { get; } = runId;
        internal string? BackupId, Operation, SavedHistoryId;
        internal string State = "idle";
        internal int? Result;
        internal DateTime? StartedUtc;
        internal ServiceActivity.Progress? Progress;
        internal ServiceRunAuthorization.AuthorizedRun? Approved;
        internal Process? Worker;
        internal TaskCompletionSource? Exited;
        internal readonly ServiceRunLease Lease = new();
        internal bool Active => Worker is not null;
    }

    private SystemBackupService(SystemService.Registration registration)
    {
        _registration = registration;
        _binding = registration.Binding;
        _measurements = new(_binding.DataDirectory);
        _installation = registration.Installation;
        Directory.CreateDirectory(_binding.DataDirectory);
        _recovery = new(_binding.DataDirectory, new LogStore(Path.Combine(_binding.DataDirectory, "logs", "engine")), new(_binding.DataDirectory));
        ServiceName = registration.ServiceName;
        CanStop = true; CanShutdown = true; AutoLog = false;
    }

    internal static int RunInstalled(string serviceName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != SystemService.LocalSystemSid || Environment.UserInteractive)
            throw new SecurityException("This command can run only as the installed Windows service.");
        var registration = SystemService.Read(serviceName, null)
            ?? throw new SecurityException("The background service record is missing.");
        var here = AppContext.BaseDirectory.TrimEnd('\\');
        if (!SystemService.SamePath(registration.Installation, here))
            throw new SecurityException("The background service record belongs to another copy of Backspace.");
        // Workers inherit these: they find their service, and the owner's
        // engines (Windows' own account has a different AppData folder).
        Environment.SetEnvironmentVariable(ServiceWorkerBinding.ServiceNameVariable, serviceName);
        if (registration.EngineRoot.Length > 0)
        {
            Environment.SetEnvironmentVariable("BACKSPACE_ENGINE_ROOT", registration.EngineRoot);
            // The owner's %LOCALAPPDATA%\Backspace: the engine's working files
            // and the service's copy of the vault key live there, on this
            // computer, not in the synced portable folder (U153).
            Environment.SetEnvironmentVariable(EngineWorkPaths.MachineRootVariable,
                Path.GetDirectoryName(registration.EngineRoot.TrimEnd('\\')));
        }
        using var service = new SystemBackupService(registration);
        Run(service);
        return service.ExitCode;
    }

    protected override void OnStart(string[] args)
    {
        Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", _binding.DataDirectory);
        Environment.SetEnvironmentVariable("BACKSPACE_NO_UPDATE_CHECK", "1");
        Environment.CurrentDirectory = _binding.DataDirectory;
        AppLogger.Initialize(new LoggingSettings());
        var log = AppLogger.For<SystemBackupService>();
        // Started by Windows while the owner had quit Backspace (a resume
        // from sleep, or a start trigger): stay stopped until the next sign-in,
        // restart or Windows start.
        if (SystemServiceLifetime.StaysQuit(_binding.DataDirectory, out var reason, () => RequestAdditionalTime(2000)))
        {
            log.Information("Staying stopped: {Reason}", reason);
            ExitCode = 0;
            _ = Task.Run(Stop);
            return;
        }
        log.Information("The background service started ({Reason})", reason);
        // Windows' own account may read every file; these let its engines
        // open files whatever their permissions, as backup programs do.
        try { ServiceWorkerPrivileges.EnableExistingBackupRights(); }
        catch (Win32Exception ex) { log.Warning(ex, "Backup rights could not be enabled; files only Windows may read are skipped"); }
        RestoreSavedStatus();
        _lifetime = Task.Run(ServeAsync);
        _leaseMonitor = Task.Run(MonitorCommandLineLeaseAsync);
    }

    private static DateTime WindowsStartedUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Why a run found unfinished at startup ended, in words: on
    /// 4 November 2026 the PC went down during a backup and the notification
    /// said only "The background worker stopped unexpectedly". A backup that
    /// resumes by itself is recorded as interrupted, without a notification
    /// (U131: the owner asked not to be bothered when nothing is needed).</summary>
    internal static string? InterruptionReason(DateTime runStartedUtc, DateTime windowsStartedUtc, bool resumes = false)
        => (windowsStartedUtc > runStartedUtc, resumes) switch
        {
            (true, true) => "Interrupted: the computer restarted, shut down or lost power during this backup. It resumes by itself; what it had already stored is kept.",
            (false, true) => "Interrupted: Backspace's background service stopped during this backup. It resumes by itself; what it had already stored is kept.",
            (true, false) => "The computer restarted, shut down or lost power during this backup, so it didn't finish. What it had already stored is kept: the next run doesn't send it again.",
            _ => null,
        };

    private string SchedulePath => Path.Combine(_binding.DataDirectory, "schedule-state.json");

    /// <summary>Marks a backup cut short so the schedule resumes it.</summary>
    private bool ResumeLater(string? backupId, RunKind kind, bool stopped)
    {
        if (kind != RunKind.Backup || backupId is null) return false;
        try { return ScheduleState.MarkCutShort(SchedulePath, backupId, stopped); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For<SystemBackupService>().Warning(ex, "A backup cut short could not be marked to resume");
            return false;
        }
    }

    /// <summary>Workers found unfinished at startup (a crash, a power loss,
    /// Windows shutting down) are recorded as interrupted, each once.</summary>
    private void RestoreSavedStatus()
    {
        try
        {
            foreach (var retained in _recovery.All().Where(c => !c.Complete))
            {
                var run = new WorkerRun(retained.HostRunId)
                {
                    BackupId = retained.BackupId, Operation = retained.Kind.ToString().ToLowerInvariant(), StartedUtc = retained.StartedUtc,
                };
                var resumes = !retained.StopRequested && ResumeLater(retained.BackupId, retained.Kind, stopped: false);
                var outcome = _recovery.Recover(retained.HostRunId, InterruptionReason(retained.StartedUtc, WindowsStartedUtc(), resumes), resumes);
                run.State = outcome?.Status == BackupRunStatus.Success ? "completed" : "interrupted";
                run.Result = run.State == "completed" ? 0 : 4;
                if (outcome is not null) run.Progress = new(retained.BackupId, 0, "", [], [], outcome, LogRunId: outcome.Id);
                Remember(run);
                SaveStatus(run);
            }
            _recovery.ForgetCompleted();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException)
        { AppLogger.For<SystemBackupService>().Warning(ex, "The previous runs' status could not be read"); }
    }

    /// <summary>The service's earlier vault, from before the portable vault
    /// (U153). Read only to return it to the owner during the conversion.</summary>
    private LegacySecretsVault Vault() => new(new DpapiSecretsEncryption(), ServiceVaultUnlock.LegacyVaultPath(_binding.DataDirectory));

    private async Task ServeAsync()
    {
        // New pipe names for this start, before any pipe exists (U152).
        // Waited out here, not in OnStart, if another program holds the file.
        try { BackupServicePipe.RenamePipes(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLogger.For<SystemBackupService>().Warning(ex, "The pipe names could not be renewed; the earlier ones are used"); }
        try
        {
            // No credential or hand-over pipe (U153): the service opens the
            // portable vault itself with its own key copy, which the owner's
            // app sends over the runs pipe ("unlock").
            var commands = ServeCommandsAsync();
            // No history or transcript pipe (U138, 6 November 2026): the
            // desktop reads this service's history and logs from the portable
            // folder, where they inherit its permissions.
            var notifications = new ServiceNotificationProtocol(new(_binding.DataDirectory)).ServeAsync(_binding, _stop.Token);
            // Due plans start together (U156); the limits decide who waits.
            var schedule = new ScheduleLoop(new(Path.Combine(_binding.DataDirectory, "schedule-state.json"),
                SchedulePlans, RunScheduledAsync, plan => ScheduleConditions.Blocked(plan, _scheduleDestinations,
                    ScheduleConditions.OnBattery, ScheduleConditions.NetworkAvailable), ScheduleConditions.OnBattery,
                MaxParallel: () => MaxWorkers)).RunAsync(_stop.Token);
            var ended = await Task.WhenAny(commands, notifications, schedule).ConfigureAwait(false);
            await ended.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Never record request or credential payloads.
            AppLogger.For<SystemBackupService>().Error(ex, "The background service stopped unexpectedly");
            ExitCode = 1066;
            _stop.Cancel();
            _ = Task.Run(Stop);
        }
    }

    /// <summary>The owner's app sends the vault key (U153). Accepted only when
    /// it opens the portable vault; the service then keeps its own copy and,
    /// the first time, counts as set up.</summary>
    private byte[] Unlock(ServiceHostRequest request)
    {
        if (AnyActive) return Reply("busy");
        byte[]? key = request.Key is null ? null : Convert.FromBase64String(request.Key);
        try
        {
            var error = ServiceVaultUnlock.Accept(_installation, _binding.DataDirectory, EngineWorkPaths.MachineRoot,
                request.VaultId, key, request.VerifyLegacy);
            if (error is not null)
            {
                AppLogger.For<SystemBackupService>().Information("The vault key was not accepted: {Reason}", error);
                return Reply(error);
            }
            if (SystemService.Read(_binding.ServiceName, _binding.OwnerSid)?.Activated != true)
            {
                SystemService.MarkActivated(_binding.ServiceName);
                AppLogger.For<SystemBackupService>().Information("The background service has the vault key; it is set up");
            }
            return Reply(null);
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private async Task ServeCommandsAsync()
    {
        using var listener = BackupServicePipe.CreateListener(_binding.ServiceName + "-runs", _binding.ServiceSid, _binding.OwnerSid);
        while (!_stop.IsCancellationRequested)
        {
            await listener.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            byte[]? request = null;
            try
            {
                request = await BackupServicePipe.ReadMessageAsync(listener, TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
                var reply = Dispatch(request);
                await BackupServicePipe.SendResponseAsync(listener, reply, TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { }
            finally
            {
                if (request is not null) CryptographicOperations.ZeroMemory(request);
                listener.Disconnect();
            }
        }
    }

    private byte[] Dispatch(byte[] bytes)
    {
        try { return DispatchCore(bytes); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or SecurityException
            or InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException
            or NotSupportedException)
        {
            AppLogger.For<SystemBackupService>().Information("Refused a request: {Reason}", ex.Message);
            return Reply("request-rejected");
        }
    }

    private bool AnyActive => _runs.Values.Any(r => r.Active);
    private WorkerRun? ActiveRun(string? runId) => runId is not null && _runs.TryGetValue(runId, out var run) && run.Active ? run : null;

    /// <summary>Busy when this backup already runs here, or when
    /// <see cref="MaxWorkers"/> workers do: the caller waits and asks again.</summary>
    private bool Busy(string backupId) => _stop.IsCancellationRequested
        || _runs.Values.Count(r => r.Active) >= MaxWorkers
        || _runs.Values.Any(r => r.Active && r.BackupId == backupId);

    private byte[] DispatchCore(byte[] bytes)
    {
        var request = ServiceHostRequest.Parse(bytes);
        lock (_gate)
        {
            switch (request.Command)
            {
                case "activity":
                    return ServiceActivity.Encode(Activity());
                case "start" or "start-cli":
                    if (!_registration.Activated && SystemService.Read(_binding.ServiceName, _binding.OwnerSid)?.Activated != true)
                        return Reply("setup-incomplete");
                    if (_returnId is not null) return Reply("removal-pending");
                    if (Busy(request.BackupId!)) return Reply("busy");
                    var approved = SystemServiceRuns.Authorize(_installation, request.BackupId!, request.Operation!, ownerCommand: request.Command == "start-cli");
                    return Reply(null, StartWorker(request, approved));
                case "cancel":
                    if (ActiveRun(request.RunId) is not { } cancelled) return Reply("not-running");
                    RecordRequestedStop(cancelled);
                    cancelled.Worker!.StandardInput.WriteLine("cancel"); cancelled.Worker.StandardInput.Flush();
                    cancelled.State = "stopping"; SaveStatus(cancelled);
                    return Reply(null, cancelled);
                case "heartbeat":
                    if (ActiveRun(request.RunId) is not { } beating)
                        return new ServiceRunJournal(_binding.DataDirectory).ReadFinished(request.RunId!) ?? Reply("not-running");
                    if (!beating.Lease.Renew(request.RunId!)) return Reply("lease-expired", beating);
                    return Reply(null, beating);
                case "freeze":
                    // Turning administrator access off: the saved sign-ins go
                    // back to the owner, so nothing may change them meanwhile.
                    if (AnyActive) return Reply("busy");
                    _returnId ??= Guid.NewGuid().ToString("N");
                    _returnStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    _ = new ServiceVaultReturnProtocol(_returnId, Path.Combine(_binding.DataDirectory, "return-" + _returnId + ".receipt"), Vault())
                        .ServeAsync(_binding, _returnStop.Token);
                    return JsonSerializer.SerializeToUtf8Bytes(new { version = 1, returnId = _returnId, state = _latest?.State ?? "idle" });
                case "measure":
                    return _measurements.Start(request.Path!, new SourceSizeRules(request.Filters, request.SkipCloudOnly));
                case "measure-result":
                    return _measurements.Result(request.RunId!);
                case "thaw":
                    _returnStop?.Cancel(); _returnStop = null; _returnId = null;
                    break;
                case "unlock":
                    return Unlock(request);
            }
            return Reply(null);
        }
    }

    /// <summary>The run started last, with every run the service knows.</summary>
    private ServiceActivity.Snapshot Activity()
    {
        var runs = _runs.Values.OrderBy(r => r.StartedUtc).Select(r => Snapshot(r, withTail: r.Active)).ToArray();
        var top = _latest is null ? new ServiceActivity.Snapshot(1, null, "idle", null, null, null, null) : Snapshot(_latest, withTail: true);
        return top with { Runs = runs };
    }

    private static ServiceActivity.Snapshot Snapshot(WorkerRun run, bool withTail) => new(1, run.RunId, run.State, run.BackupId,
        run.Operation, run.StartedUtc, withTail || run.Progress is null ? run.Progress : run.Progress with { TranscriptTail = "" });

    private void Remember(WorkerRun run)
    {
        // A backup's earlier finished run makes way for this one.
        foreach (var old in _runs.Values.Where(r => !r.Active && r.BackupId == run.BackupId && r.RunId != run.RunId).ToList())
            _runs.Remove(old.RunId);
        _runs[run.RunId] = run;
        if (_latest is null || run.StartedUtc >= _latest.StartedUtc) _latest = run;
    }

    private void RecordRequestedStop(WorkerRun run)
    {
        try { _recovery.StopRequested(run.RunId); }
        catch (Exception ex) { AppLogger.For<SystemBackupService>().Warning(ex, "Stop will proceed although its saved reason could not be updated"); }
    }

    private WorkerRun StartWorker(ServiceHostRequest request, ServiceRunAuthorization.AuthorizedRun approved)
    {
        var run = new WorkerRun(Guid.NewGuid().ToString("N"))
        {
            State = "starting", BackupId = request.BackupId, Operation = request.Operation, Approved = approved, StartedUtc = DateTime.UtcNow,
        };
        var start = new ProcessStartInfo(Path.Combine(_installation, "Backspace.exe")) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = _binding.DataDirectory, RedirectStandardInput = true, RedirectStandardOutput = true };
        // stdout is the private framed worker feed, never a console log sink.
        start.Environment["BACKSPACE_DEBUG"] = "0";
        // The run's identity is the worker's revision: its reason file is named after it.
        foreach (var argument in new[] { request.Command == "start-cli" ? "--service-worker-cli" : "--service-worker-owned",
            run.RunId, request.BackupId!, request.Operation! })
            start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        var started = false;
        Remember(run);
        try
        {
            _recovery.Begin(new(1, run.RunId, approved.Backup.Id, approved.Backup.Name, approved.Kind,
                run.StartedUtc!.Value, request.Command is "scheduled" or "start-cli", approved.Configuration.Notifications.OnFailure.Toast,
                approved.Configuration.Notifications.OnFailure.Sound));
            SaveStatus(run);
            if (!process.Start()) throw new IOException("The backup worker could not start.");
            started = true;
            // The child waits for 'start' before touching a repository.
            if (!KillOnExitJobObject.TryAssign(process)) throw new IOException("The worker lifetime could not be protected.");
            run.State = "running";
            SaveStatus(run);
            run.Worker = process;
            run.Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (request.Command == "start-cli") run.Lease.Begin(run.RunId); else run.Lease.Clear();
            process.StandardInput.WriteLine("start"); process.StandardInput.Flush();
            _ = ObserveWorkerAsync(run, process, ReadProgressAsync(run, process, request.BackupId!));
            return run;
        }
        catch
        {
            var stopped = !started;
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); stopped = process.WaitForExit(2000); }
                catch { /* An uncertain child keeps the slot. */ }
            }
            if (stopped) { process.Dispose(); run.Worker = null; run.State = "failed-to-start"; run.Lease.Clear(); }
            else
            {
                run.Worker = process; run.State = "unknown";
                run.Exited ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = ObserveWorkerAsync(run, process, ReadProgressAsync(run, process, request.BackupId!));
            }
            try { SaveStatus(run); } catch { /* Preserve the startup failure. */ }
            if (stopped) try { _ = _recovery.Recover(run.RunId); } catch (Exception ex) { AppLogger.For<SystemBackupService>().Error(ex, "The failed startup remains pending in its recovery record"); }
            throw;
        }
    }

    private async Task MonitorCommandLineLeaseAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                List<(WorkerRun Run, Process Worker)> expired;
                lock (_gate)
                {
                    expired = _runs.Values.Where(r => r.Active && r.Lease.IsExpired && r.State != "stopping")
                        .Select(r => (r, r.Worker!)).ToList();
                    foreach (var (run, worker) in expired)
                    {
                        run.State = "stopping";
                        try { SaveStatus(run); } catch (IOException) { }
                        try { worker.StandardInput.WriteLine("cancel"); worker.StandardInput.Flush(); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
                    }
                }
                foreach (var (run, worker) in expired)
                    _ = KillIfStuckAsync(run, worker);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task KillIfStuckAsync(WorkerRun run, Process worker)
    {
        try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
        catch (System.TimeoutException)
        { lock (_gate) if (ReferenceEquals(run.Worker, worker) && !worker.HasExited) worker.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    // Read until the worker's output ends, also while the service stops: a
    // stopping worker still writes its last progress and the run's record,
    // and with nobody reading, its pipe filled and it hung until the service
    // killed it 15 s later. Quit looked as if it had not worked, and the
    // run's final report was lost (U126, 4 November 2026).
    private async Task ReadProgressAsync(WorkerRun run, Process process, string backupId)
    {
        try
        {
            while (true)
            {
                var bytes = await BackupServicePipe.ReadMessageAsync(process.StandardOutput.BaseStream, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
                var progress = ServiceActivity.ParseProgress(bytes, backupId);
                lock (_gate) if (ReferenceEquals(process, run.Worker))
                {
                    run.Progress = progress;
                    if (progress.LogRunId is { } historyId && run.SavedHistoryId != historyId)
                    {
                        try { _recovery.TrackHistory(run.RunId, historyId); run.SavedHistoryId = historyId; }
                        catch (Exception ex) { AppLogger.For<SystemBackupService>().Warning(ex, "The live run identity could not yet be retained"); }
                    }
                }
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { AppLogger.For<SystemBackupService>().Warning(ex, "Live worker activity could not be read"); }
    }

    private async Task ObserveWorkerAsync(WorkerRun run, Process process, Task progress)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        try { await progress.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (System.TimeoutException) { }
        lock (_gate)
        {
            run.Result = process.ExitCode; run.State = run.Result == 0 ? "completed" : "failed";
            try
            {
                if (run.Progress?.Result is { } known) { _recovery.TrackHistory(run.RunId, known.Id); _recovery.Confirm(run.RunId); }
                // A check reports no live result, but saves its own history:
                // a clean exit is that result, not an interruption (lab VM,
                // 2 November 2026: a successful check also showed "stopped unexpectedly").
                else if (process.ExitCode == 0 && SavedOutcome(run) is { } saved)
                {
                    _recovery.TrackHistory(run.RunId, saved.Id); _recovery.Confirm(run.RunId);
                    run.Progress = new(saved.BackupId, 0, "", [], [], saved, LogRunId: saved.Id);
                }
                else if (_recovery.Recover(run.RunId, FailureReason(run)) is { } recovered && run.BackupId is not null)
                    run.Progress = new(run.BackupId, 0, "", [], [], recovered, LogRunId: recovered.Id);
            }
            catch (Exception ex) { AppLogger.For<SystemBackupService>().Error(ex, "The interrupted worker outcome remains pending for recovery"); }
            try { SaveStatus(run); } catch (Exception ex) { AppLogger.For<SystemBackupService>().Error(ex, "Could not save service worker exit"); }
            run.Worker = null; run.Lease.Clear(); process.Dispose();
            run.Exited?.TrySetResult(); run.Exited = null;
        }
    }

    private RunRecord? SavedOutcome(WorkerRun run)
    {
        if (run.BackupId is null || run.StartedUtc is not { } started || run.Approved is not { } approved) return null;
        try
        {
            return new LogStore(Path.Combine(_binding.DataDirectory, "logs", "engine")).LoadTimelineByBackupId(run.BackupId)
                .Where(r => r.Kind == approved.Kind && r.StartedUtc >= started.AddSeconds(-5) && r.EndedUtc is not null
                    && r.Status != BackupRunStatus.Running && Guid.TryParseExact(r.Id, "N", out _))
                .OrderBy(r => r.StartedUtc).FirstOrDefault();
        }
        catch (Exception ex) { AppLogger.For<SystemBackupService>().Warning(ex, "Could not read the finished run's history"); return null; }
    }

    private string? FailureReason(WorkerRun run)
    {
        try
        {
            var path = Path.Combine(_binding.DataDirectory, ServiceNetworkShares.ReasonFileFor(run.RunId));
            if (File.Exists(path) && new FileInfo(path).Length is > 0 and < 2048)
            {
                var reason = File.ReadAllText(path).Trim();
                File.Delete(path);
                if (reason.Length > 0) return reason;
            }
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { return run.Approved is { } approved ? ServiceApprovedWorker.MissingDestination(approved) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The owner's backups as saved, re-read every minute; null while
    /// setup or removal is unfinished or the settings cannot be read.</summary>
    private IReadOnlyList<SchedulePlan>? SchedulePlans()
    {
        if (_returnId is not null) return null;
        if (!_registration.Activated && SystemService.Read(_binding.ServiceName, _binding.OwnerSid)?.Activated != true) return null;
        AppConfig config;
        try { config = ServiceConfigurationComparison.ReadDesktop(_installation); _unreadableSettingsLogged = false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            if (!_unreadableSettingsLogged) AppLogger.For<SystemBackupService>().Warning(ex, "Scheduled backups wait until the saved settings can be read");
            _unreadableSettingsLogged = true;
            return null;
        }
        _scheduleDestinations = config.Destinations;
        return SchedulePlan.From(config).Where(p => SystemServiceRuns.CanRun(p.Backup, config)).ToList();
    }

    private async Task<bool> RunScheduledAsync(SchedulePlan plan, CancellationToken ct)
    {
        Task exited;
        WorkerRun run;
        lock (_gate)
        {
            if (Busy(plan.Backup.Id) || _returnId is not null) return false;
            var operation = plan.Kind.ToString().ToLowerInvariant();
            var approved = SystemServiceRuns.Authorize(_installation, plan.Backup.Id, operation, ownerCommand: false);
            run = StartWorker(new ServiceHostRequest(1, "scheduled", null, plan.Backup.Id, operation), approved);
            exited = run.Exited?.Task ?? Task.CompletedTask;
        }
        // A time limit or battery stops the worker, then waits for it. When
        // the service itself stops, OnStop tells the worker why (U131).
        using (ct.Register(() =>
        {
            if (_stop.IsCancellationRequested) return;
            lock (_gate) try { run.Worker?.StandardInput.WriteLine("cancel"); run.Worker?.StandardInput.Flush(); } catch (Exception) { }
        }))
            await exited.ConfigureAwait(false);
        return true;
    }

    private byte[] Reply(string? error, WorkerRun? run = null)
    {
        run ??= _latest;
        return JsonSerializer.SerializeToUtf8Bytes(new { version = 1, runId = run?.RunId, state = run?.State ?? "idle", exitCode = run?.Result, error });
    }

    private void SaveStatus(WorkerRun run) => new ServiceRunJournal(_binding.DataDirectory).Save(Reply(null, run));

    protected override void OnStop()
    {
        _stop.Cancel();
        List<Process> children;
        lock (_gate)
        {
            children = [];
            foreach (var run in _runs.Values.Where(r => r.Active))
            {
                RecordRequestedStop(run);
                // Windows shutting down or restarting, an update or Quit: a
                // backup is resumed when the service runs again, and its
                // history says "Interrupted", not "Stopped" (U131).
                var resumes = run.Approved is { } approved && ResumeLater(run.BackupId, approved.Kind, stopped: true);
                try { run.Worker!.StandardInput.WriteLine(resumes ? "interrupt" : "cancel"); run.Worker.StandardInput.Flush(); } catch (IOException) { }
                children.Add(run.Worker!);
            }
        }
        if (children.Count > 0)
        {
            RequestAdditionalTime(20000);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            foreach (var child in children)
            {
                try
                {
                    var left = deadline - DateTime.UtcNow;
                    if (!child.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero)) child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
            }
        }
        try { _lifetime?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        try { _leaseMonitor?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        AppLogger.For<SystemBackupService>().Information("The background service stopped");
    }

    protected override void OnShutdown() => OnStop();
}
