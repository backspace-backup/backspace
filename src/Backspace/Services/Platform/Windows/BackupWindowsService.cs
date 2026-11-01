using System;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>SCM lifetime for the protected service. Installation and scheduling
/// are separate; this host never reads an interactive account's DPAPI vault.</summary>
internal sealed class BackupWindowsService : ServiceBase
{
    private readonly ServiceWorkerBinding _binding;
    private readonly string _installation;
    private readonly CancellationTokenSource _stop = new();
    private Task? _lifetime;
    private Process? _worker;
    private readonly object _gate = new();
    private string? _runId;
    private string _state = "idle";
    private int? _result;
    private string? _backupId, _operation;
    private ServiceRunAuthorization.AuthorizedRun? _approved;
    private DateTime? _startedUtc;
    private ServiceActivity.Progress? _progress;
    private string? _savedHistoryId;
    private readonly ServiceRunLease _scheduledLease = new();
    private Task? _leaseMonitor;
    private SessionTaskControl.UnattendedLease? _sessionLease;
    private CancellationTokenRegistration _sessionCancellation;
    private readonly ServiceRunRecovery _recovery;
    private TaskCompletionSource? _workerExited;
    private string? _scheduleRevision;
    private System.Collections.Generic.IReadOnlyList<Models.Destination> _scheduleDestinations = [];
    private bool _waitingForSettings;

    private SessionTaskControl SessionControl(int session = 0) => new(_binding.DataDirectory,
        new WindowsInteractiveSessionReader(_binding.OwnerSid), externallyCheckedSession: true);

    private BackupWindowsService(string installation, ServiceWorkerBinding binding)
    {
        _installation = installation; _binding = binding;
        _recovery = new(binding.DataDirectory, new LogStore(Path.Combine(binding.DataDirectory, "logs", "engine")), new(binding.DataDirectory));
        ServiceName = binding.ServiceName;
        CanStop = true; CanShutdown = true; AutoLog = false;
        // Sign-out ends a Quit pause (U043).
        CanHandleSessionChangeEvent = true;
    }

    internal static int RunInstalled()
    {
        var installation = ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\'));
        var binding = ServiceWorkerBinding.Load(installation);
        binding.ValidateWorkerIdentity();
        ServiceInstallationSecurity.ValidateCodeTree(installation, binding.DataDirectory, binding.ServiceSid);
        using var service = new BackupWindowsService(installation, binding);
        Run(service);
        return service.ExitCode;
    }

    protected override void OnStart(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("Service startup does not accept execution arguments.");
        Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", _binding.DataDirectory);
        Environment.SetEnvironmentVariable("BACKSPACE_NO_UPDATE_CHECK", "1");
        Environment.CurrentDirectory = _binding.DataDirectory;
        // The service keeps the schedule (U043); its decisions belong in the
        // service's own log, beside its workers' (a shared, multi-process file).
        AppLogger.Initialize(new Models.LoggingSettings());
        var savedPath = Path.Combine(_binding.DataDirectory, "service-run-status.json");
        if (File.Exists(savedPath))
        {
            using var saved = JsonDocument.Parse(File.ReadAllBytes(savedPath));
            _runId = saved.RootElement.GetProperty("runId").GetString();
            _state = saved.RootElement.GetProperty("state").GetString() ?? "unknown";
            _result = saved.RootElement.GetProperty("exitCode").ValueKind == JsonValueKind.Number
                ? saved.RootElement.GetProperty("exitCode").GetInt32() : null;
            if (_state is "starting" or "running" or "stopping") { _state = "interrupted"; SaveStatus(); }
        }
        if (_recovery.Read() is { } retained && (!retained.Complete || retained.HostRunId == _runId && _state == "interrupted"))
        {
            _runId = retained.HostRunId; _backupId = retained.BackupId;
            _operation = retained.Kind.ToString().ToLowerInvariant(); _startedUtc = retained.StartedUtc;
            var outcome = _recovery.Recover(retained.HostRunId);
            _state = outcome?.Status == Models.BackupRunStatus.Success ? "completed" : "interrupted";
            _result = _state == "completed" ? 0 : 4;
            if (outcome is not null) _progress = new(retained.BackupId, 0, "", [], [], outcome, LogRunId: outcome.Id);
            SaveStatus();
        }
        _lifetime = Task.Run(ServeAsync);
        _leaseMonitor = Task.Run(MonitorScheduledLeaseAsync);
    }

    private async Task ServeAsync()
    {
        try
        {
            var vault = new SecretsStore(new DpapiSecretsEncryption(), Path.Combine(_binding.DataDirectory, "secrets.bin"));
            var migration = ServiceMigrationProtocol.Load(_installation, _binding, vault);
            if (migration is null) vault.WithMigrationSnapshot(_ => true, requireExisting: true);
            var retiring = ServiceRetirement.Read(_installation);
            var secrets = ServiceSecretsProtocol.ServeAsync(_binding.ServiceName + "-secrets", _binding.ServiceSid, _binding.OwnerSid, vault, _stop.Token,
                () => ServiceRetirement.Read(_installation) is not null);
            var commands = ServeCommandsAsync();
            var sessions = new ServiceSessionProtocol(SessionControl,
                (session, claimed) => ServiceSessionProtocol.MatchesOwnerSession(session, claimed, _binding.OwnerSid))
                .ServeAsync(_binding, _stop.Token);
            var history = new ServiceHistoryProtocol(new LogStore().LoadTimelineByBackupId)
                .ServeAsync(_binding.ServiceName + "-history", _binding.ServiceSid, _binding.OwnerSid, _stop.Token);
            var transcripts = new ServiceTranscriptProtocol(AppPaths.EngineLogsRoot, new LogStore().LoadTimelineByBackupId)
                .ServeAsync(_binding, _stop.Token);
            var notifications = new ServiceNotificationProtocol(new(_binding.DataDirectory)).ServeAsync(_binding, _stop.Token);
            var handoff = migration is null ? Task.Delay(Timeout.Infinite, _stop.Token)
                : migration.ServeAsync(_binding.ServiceName + "-migration", _binding.ServiceSid, _binding.OwnerSid, _stop.Token);
            var returning = retiring is { Stage: "frozen" }
                ? new ServiceVaultReturnProtocol(retiring.ReturnId, ServiceRetirement.ReceiptPath(retiring), vault).ServeAsync(_binding, _stop.Token)
                : Task.Delay(Timeout.Infinite, _stop.Token);
            var schedule = new ScheduleLoop(new(Path.Combine(_binding.DataDirectory, "schedule-state.json"),
                SchedulePlans, RunScheduledAsync, plan => ScheduleConditions.Blocked(plan, _scheduleDestinations,
                    ScheduleConditions.OnBattery, ScheduleConditions.NetworkAvailable), ScheduleConditions.OnBattery)).RunAsync(_stop.Token);
            var ended = await Task.WhenAny(secrets, commands, handoff, history, transcripts, notifications, sessions, returning, schedule).ConfigureAwait(false);
            await ended.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Never record request or credential payloads.
            AppLogger.For<BackupWindowsService>().Error(ex, "The protected service stopped unexpectedly");
            ExitCode = 1066;
            _stop.Cancel();
            _ = Task.Run(Stop);
        }
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
        // Keep filtered handlers outside the method owning the monitor. See
        // ServiceMigrationProtocol's Release cross-thread retry regression.
        try { return DispatchCore(bytes); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or System.Security.SecurityException
            or InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        { return Reply("request-rejected"); }
    }

    private byte[] DispatchCore(byte[] bytes)
    {
        var request = ServiceHostRequest.Parse(bytes);
        lock (_gate)
        {
            if (request.Command == "activity")
                return ServiceActivity.Encode(new ServiceActivity.Snapshot(1, _runId, _state, _backupId, _operation, _startedUtc, _progress));
            if (request.Command is "start" or "start-cli")
            {
                ServiceRetirement.RequireNotRetiring(_installation);
                if (!ServiceWorkerBinding.Load(_installation).CanRun)
                    return Reply("setup-incomplete");
                if (ServiceConfigurationUpdate.IsPending(_installation, _binding)) return Reply("settings-update-incomplete");
                if (ServiceProgramUpdate.IsPending(_installation)) return Reply("program-update-incomplete");
                if (_worker is not null || _stop.IsCancellationRequested) return Reply("busy");
                var policy = ServiceRunAuthorization.Load(ServicePortableLayout.ExistingRecord(_installation, "service-approval.json"), _installation, _binding.OwnerSid);
                var approved = policy.Authorize(JsonSerializer.SerializeToUtf8Bytes(new ServiceRunAuthorization.Request(1, request.Revision!, request.BackupId!, request.Operation!), ServiceRunAuthorization.Json), ownerCommand: request.Command == "start-cli");
                if (request.Command == "start-cli")
                {
                    // Admission and Quit share the service's private transaction.
                    // The running handle stays held until the owned worker exits.
                    if (!SessionControl().TryBeginUnattended(out _sessionLease,
                        () => { QueueSessionStop(force: true); return Task.CompletedTask; }))
                        return JsonSerializer.SerializeToUtf8Bytes(new ServiceScheduledRun.Response(1, null, "paused", 0, null), ServiceRunAuthorization.Json);
                    _sessionCancellation = _sessionLease!.Token.Register(() => QueueSessionStop(force: false));
                }
                try { StartWorker(request, approved); }
                catch { if (_worker is null) ReleaseSessionLease(); throw; }
            }
            else if (request.Command == "cancel")
            {
                if (_worker is null || request.RunId != _runId) return Reply("not-running");
                RecordRequestedStop();
                _worker.StandardInput.WriteLine("cancel"); _worker.StandardInput.Flush();
                _state = "stopping"; SaveStatus();
            }
            else if (request.Command == "heartbeat")
            {
                if (_worker is null || request.RunId != _runId)
                    return new ServiceRunJournal(_binding.DataDirectory).ReadFinished(request.RunId!) ?? Reply("not-running");
                if (!_scheduledLease.Renew(request.RunId!)) return Reply("lease-expired");
            }
            return Reply(null);
        }
    }

    private void QueueSessionStop(bool force)
    {
        // Never take _gate synchronously from the pause monitor: worker cleanup
        // can be disposing that monitor while it owns the same gate.
        var expected = _sessionLease;
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                if (expected is null || !ReferenceEquals(expected, _sessionLease) || _worker is null) return;
                try
                {
                    _state = "stopping";
                    RecordRequestedStop();
                    try { SaveStatus(); } catch (IOException) { }
                    if (force) _worker.Kill(entireProcessTree: true);
                    else { _worker.StandardInput.WriteLine("cancel"); _worker.StandardInput.Flush(); }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
                { AppLogger.For<BackupWindowsService>().Warning(ex, "Could not deliver the session Quit request to its worker"); }
            }
        });
    }

    private void ReleaseSessionLease()
    {
        _sessionCancellation.Dispose();
        _sessionLease?.Dispose(); _sessionLease = null;
    }

    private void RecordRequestedStop()
    {
        try { _recovery.StopRequested(_runId); }
        catch (Exception ex) { AppLogger.For<BackupWindowsService>().Warning(ex, "Stop will proceed although its saved reason could not be updated"); }
    }

    private void StartWorker(ServiceHostRequest request, ServiceRunAuthorization.AuthorizedRun approved)
    {
        // A failed disk write after the preceding exit must be recovered before
        // replacing its context with a new worker's identity.
        if (_recovery.Read() is { Complete: false } prior) _recovery.Recover(prior.HostRunId);
        var executable = Path.Combine(_installation, "Backspace.exe");
        ServiceInstallationSecurity.ValidateCodeFile(executable, _installation);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = _binding.DataDirectory, RedirectStandardInput = true, RedirectStandardOutput = true };
        // stdout is the private framed worker feed, never a console log sink.
        start.Environment["BACKSPACE_DEBUG"] = "0";
        foreach (var argument in new[] { request.Command == "start-cli" ? "--service-worker-cli" : "--service-worker-owned", request.Revision!, request.BackupId!, request.Operation! })
            start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        var started = false;
        // Allocate the new identity before any launch attempt. A failed
        // Process.Start must not overwrite the preceding run's identity/result.
        _runId = Guid.NewGuid().ToString("N"); _state = "starting"; _result = null;
        _backupId = request.BackupId; _operation = request.Operation; _approved = approved;
        _startedUtc = DateTime.UtcNow; _progress = null; _savedHistoryId = null;
        try
        {
            _recovery.Begin(new(1, _runId, approved.Backup.Id, approved.Backup.Name, approved.Kind,
                _startedUtc.Value, request.Command is "scheduled" or "start-cli", approved.Configuration.Notifications.OnFailure.Toast,
                approved.Configuration.Notifications.OnFailure.Sound));
            SaveStatus();
            if (!process.Start()) throw new IOException("The approved worker could not start.");
            started = true;
            // The child waits for 'start' before touching a repository. Assign
            // its process tree to the lifetime job before releasing that gate.
            if (!KillOnExitJobObject.TryAssign(process)) throw new IOException("The worker lifetime could not be protected.");
            _state = "running";
            SaveStatus();
            _worker = process;
            _workerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // A command-line client keeps its run alive with heartbeats. The
            // service's own schedule needs none.
            if (request.Command == "start-cli") _scheduledLease.Begin(_runId);
            else _scheduledLease.Clear();
            process.StandardInput.WriteLine("start"); process.StandardInput.Flush();
            _ = ObserveWorkerAsync(process, ReadProgressAsync(process, request.BackupId!));
        }
        catch
        {
            var stopped = !started;
            if (started)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    stopped = process.WaitForExit(2000);
                }
                catch { /* An uncertain child retains the slot and lifetime job. */ }
            }
            if (stopped)
            {
                process.Dispose(); _worker = null; _state = "failed-to-start"; _scheduledLease.Clear();
                ReleaseSessionLease();
            }
            else
            {
                _worker = process; _state = "unknown";
                _ = ObserveWorkerAsync(process, ReadProgressAsync(process, request.BackupId!));
            }
            try { SaveStatus(); } catch { /* Preserve the startup failure. */ }
            if (stopped) try { _ = _recovery.Recover(_runId); } catch (Exception ex) { AppLogger.For<BackupWindowsService>().Error(ex, "The failed startup remains pending in its recovery record"); }
            throw;
        }
    }

    private async Task MonitorScheduledLeaseAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
                Process? expired;
                lock (_gate)
                {
                    expired = _scheduledLease.IsExpired ? _worker : null;
                    if (expired is null) continue;
                    _state = "stopping";
                    try { SaveStatus(); } catch (IOException) { }
                    try { expired.StandardInput.WriteLine("cancel"); expired.StandardInput.Flush(); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
                }
                // Keep the worker slot occupied until ObserveWorker confirms
                // exit. No new run can race a still-stopping engine.
                try { await expired.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                catch (System.TimeoutException)
                {
                    lock (_gate)
                        if (ReferenceEquals(_worker, expired) && !expired.HasExited)
                            expired.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { /* Observer already disposed the exited worker. */ }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppLogger.For<BackupWindowsService>().Error(ex, "The scheduled worker lifetime could not be enforced");
            ExitCode = 1066; _stop.Cancel(); _ = Task.Run(Stop);
        }
    }

    private async Task ReadProgressAsync(Process process, string backupId)
    {
        try
        {
            while (true)
            {
                var bytes = await BackupServicePipe.ReadMessageAsync(process.StandardOutput.BaseStream, TimeSpan.FromMinutes(1), _stop.Token).ConfigureAwait(false);
                var progress = ServiceActivity.ParseProgress(bytes, backupId);
                lock (_gate) if (ReferenceEquals(process, _worker))
                {
                    _progress = progress;
                    if (progress.LogRunId is { } historyId && _runId is not null && _savedHistoryId != historyId)
                    {
                        try { _recovery.TrackHistory(_runId, historyId); _savedHistoryId = historyId; }
                        catch (Exception ex) { AppLogger.For<BackupWindowsService>().Warning(ex, "The live run identity could not yet be retained"); }
                    }
                }
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        { AppLogger.For<BackupWindowsService>().Warning(ex, "Live worker activity could not be read"); }
    }

    private async Task ObserveWorkerAsync(Process process, Task progress)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        try { await progress.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (System.TimeoutException) { /* Child exited; stale progress cannot keep the run open. */ }
        lock (_gate)
        {
            _result = process.ExitCode; _state = _result == 0 ? "completed" : "failed";
            try
            {
                if (_progress?.Result is { } known && _runId is not null)
                { _recovery.TrackHistory(_runId, known.Id); _recovery.Confirm(_runId); }
                else if (_recovery.Recover(_runId, FailureReason()) is { } recovered && _backupId is not null)
                    _progress = new(_backupId, 0, "", [], [], recovered, LogRunId: recovered.Id);
            }
            catch (Exception ex) { AppLogger.For<BackupWindowsService>().Error(ex, "The interrupted worker outcome remains pending for recovery"); }
            try { SaveStatus(); }
            catch (Exception ex) { AppLogger.For<BackupWindowsService>().Error(ex, "Could not save service worker exit"); }
            _worker = null; _scheduledLease.Clear(); process.Dispose();
            ReleaseSessionLease();
            _workerExited?.TrySetResult(); _workerExited = null;
        }
    }

    private string? FailureReason()
    {
        try { return _approved is { } run ? ServiceApprovedWorker.MissingDestination(run) : null; }
        catch (Exception ex) { AppLogger.For<BackupWindowsService>().Warning(ex, "Could not check the destination of a failed run"); return null; }
    }

    /// <summary>The approved backups, or null while the schedule must wait:
    /// during an update, removal or settings change, or while the owner's
    /// saved settings differ from the approved ones and are being applied.</summary>
    private System.Collections.Generic.IReadOnlyList<SchedulePlan>? SchedulePlans()
    {
        ExpireEndedPause(_ => false);
        if (ServiceRetirement.Read(_installation) is not null || ServiceProgramUpdate.IsPending(_installation)
            || ServiceConfigurationUpdate.IsPending(_installation, _binding) || !ServiceWorkerBinding.Load(_installation).CanRun) return null;
        var approval = ServiceRunAuthorization.Load(ServicePortableLayout.ExistingRecord(_installation, "service-approval.json"),
            _installation, _binding.OwnerSid).Snapshot();
        bool current;
        // The owner's settings folder keeps the owner's permissions. When this
        // account may not read it, the approved settings are the reference.
        try { current = ServiceConfigurationComparison.Equivalent(approval.Configuration, ServiceConfigurationComparison.ReadDesktop(_installation)); }
        catch (UnauthorizedAccessException) { current = true; }
        // Missing, being replaced or unreadable: wait and look again.
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { current = false; }
        if (!current)
        {
            if (!_waitingForSettings) AppLogger.For<BackupWindowsService>().Information("Scheduled backups wait until the latest saved settings are applied");
            _waitingForSettings = true;
            return null;
        }
        _waitingForSettings = false;
        _scheduleRevision = approval.Revision;
        _scheduleDestinations = approval.Configuration.Destinations;
        return System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(SchedulePlan.From(approval.Configuration),
            p => SchedulePlan.ServiceCanRun(p.Backup, approval.Configuration.Destinations)));
    }

    private async Task<bool> RunScheduledAsync(SchedulePlan plan, CancellationToken ct)
    {
        Task exited;
        lock (_gate)
        {
            if (_worker is not null || _stop.IsCancellationRequested || _scheduleRevision is null) return false;
            var operation = plan.Kind.ToString().ToLowerInvariant();
            var request = new ServiceHostRequest(1, "scheduled", _scheduleRevision, plan.Backup.Id, operation);
            var policy = ServiceRunAuthorization.Load(ServicePortableLayout.ExistingRecord(_installation, "service-approval.json"), _installation, _binding.OwnerSid);
            var approved = policy.Authorize(JsonSerializer.SerializeToUtf8Bytes(
                new ServiceRunAuthorization.Request(1, _scheduleRevision, plan.Backup.Id, operation), ServiceRunAuthorization.Json));
            // Quit pauses until sign-out or restart; the run stays due meanwhile.
            if (!SessionControl().TryBeginUnattended(out _sessionLease,
                () => { QueueSessionStop(force: true); return Task.CompletedTask; })) return false;
            _sessionCancellation = _sessionLease!.Token.Register(() => QueueSessionStop(force: false));
            try { StartWorker(request, approved); }
            catch { if (_worker is null) ReleaseSessionLease(); throw; }
            exited = _workerExited?.Task ?? Task.CompletedTask;
        }
        // A time limit or battery stops the worker as Quit does, then waits for it.
        using (ct.Register(() => QueueSessionStop(force: false)))
            await exited.ConfigureAwait(false);
        return true;
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        if (change.Reason == SessionChangeReason.SessionLogoff)
            ExpireEndedPause(pause => pause.Session!.SessionId == change.SessionId);
    }

    /// <summary>Quit pauses the schedule for one Windows sign-in session.
    /// Sign-out (reported by Windows), a restart, or the session's absence
    /// ends it; sleep and hibernation do not.</summary>
    private void ExpireEndedPause(Func<SessionTaskControl.PauseState, bool> signedOut)
    {
        try
        {
            var boot = WindowsInteractiveSessionReader.CurrentBootId();
            if (SessionControl().EndPauseIf(pause => signedOut(pause)
                    || pause.BootId is { } saved && boot is { } current && saved != current
                    || WindowsInteractiveSessionReader.SessionIsAbsent(pause.Session!.SessionId)))
                AppLogger.For<BackupWindowsService>().Information("The Windows session that quit Backspace has ended; scheduled backups resume");
        }
        catch (Exception ex) { AppLogger.For<BackupWindowsService>().Warning(ex, "Could not check whether the paused sign-in session has ended"); }
    }

    private byte[] Reply(string? error) => JsonSerializer.SerializeToUtf8Bytes(new { version = 1, runId = _runId, state = _state, exitCode = _result, error });
    private void SaveStatus()
    {
        new ServiceRunJournal(_binding.DataDirectory).Save(Reply(null));
    }

    protected override void OnStop()
    {
        _stop.Cancel();
        Process? child;
        lock (_gate)
        {
            child = _worker;
            if (child is not null)
            { try { child.StandardInput.WriteLine("cancel"); child.StandardInput.Flush(); } catch (IOException) { } }
        }
        if (child is not null)
        {
            RequestAdditionalTime(20000);
            try { if (!child.WaitForExit(15000)) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* The observer already disposed a finished child. */ }
        }
        try { _lifetime?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        try { _leaseMonitor?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
    }
    protected override void OnShutdown() => OnStop();
}

internal sealed record ServiceHostRequest(int Version, string Command, string? Revision = null,
    string? BackupId = null, string? Operation = null, string? RunId = null)
{
    internal static ServiceHostRequest Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 4096) throw new InvalidDataException("Invalid run request size.");
        var reader = new Utf8JsonReader(bytes);
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) if (reader.TokenType == JsonTokenType.PropertyName && !names.Add(reader.GetString()!))
            throw new JsonException("Duplicate property.");
        var request = JsonSerializer.Deserialize<ServiceHostRequest>(bytes, ServiceRunAuthorization.Json)
            ?? throw new JsonException("Empty request.");
        if (request.Version != 1 || (request.Command switch
        {
            "status" or "activity" => request.Revision is not null || request.BackupId is not null || request.Operation is not null || request.RunId is not null,
            "cancel" or "heartbeat" => !Guid.TryParseExact(request.RunId, "N", out _) || request.Revision is not null || request.BackupId is not null || request.Operation is not null,
            "start" or "start-cli" => !Guid.TryParseExact(request.Revision, "N", out _) || string.IsNullOrWhiteSpace(request.BackupId)
                || request.Operation is not ("backup" or "check" or "prune") || request.RunId is not null,
            _ => true
        })) throw new InvalidDataException("Unsupported run request.");
        return request;
    }
}
