using System;
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
/// separate approval), keeps the schedule, holds the saved sign-ins once they
/// are handed over, and runs each backup in a worker process. Quit stops it;
/// it starts again at the next sign-in, restart or Windows start
/// (<see cref="SystemServiceLifetime"/>).
/// </summary>
internal sealed class SystemBackupService : ServiceBase
{
    private readonly SystemService.Registration _registration;
    private readonly ServiceWorkerBinding _binding;
    private readonly string _installation;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly ServiceSizeMeasurements _measurements = new();
    private readonly ServiceRunLease _cliLease = new();
    private readonly ServiceRunRecovery _recovery;
    private Task? _lifetime, _leaseMonitor;
    private Process? _worker;
    private TaskCompletionSource? _workerExited;
    private string? _runId, _backupId, _operation, _savedHistoryId;
    private string _state = "idle";
    private int? _result;
    private DateTime? _startedUtc;
    private ServiceActivity.Progress? _progress;
    private ServiceRunAuthorization.AuthorizedRun? _approved;
    private System.Collections.Generic.IReadOnlyList<Destination> _scheduleDestinations = [];
    private string? _returnId;
    private CancellationTokenSource? _returnStop;
    private bool _unreadableSettingsLogged;

    private SystemBackupService(SystemService.Registration registration)
    {
        _registration = registration;
        _binding = registration.Binding;
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
        if (registration.EngineRoot.Length > 0) Environment.SetEnvironmentVariable("BACKSPACE_ENGINE_ROOT", registration.EngineRoot);
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
        if (SystemServiceLifetime.StaysQuit(_binding.DataDirectory, out var reason))
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

    private void RestoreSavedStatus()
    {
        var savedPath = Path.Combine(_binding.DataDirectory, "service-run-status.json");
        try
        {
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
                _state = outcome?.Status == BackupRunStatus.Success ? "completed" : "interrupted";
                _result = _state == "completed" ? 0 : 4;
                if (outcome is not null) _progress = new(retained.BackupId, 0, "", [], [], outcome, LogRunId: outcome.Id);
                SaveStatus();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or System.Collections.Generic.KeyNotFoundException)
        { AppLogger.For<SystemBackupService>().Warning(ex, "The previous run's status could not be read"); }
    }

    private SecretsStore Vault() => new(new DpapiSecretsEncryption(), Path.Combine(_binding.DataDirectory, "secrets.bin"));

    private async Task ServeAsync()
    {
        try
        {
            var vault = Vault();
            var secrets = ServiceSecretsProtocol.ServeAsync(_binding.ServiceName + "-secrets", _binding.ServiceSid, _binding.OwnerSid, vault, _stop.Token,
                () => _returnId is not null);
            var commands = ServeCommandsAsync();
            var handoff = Handoff(vault)?.ServeAsync(_binding.ServiceName + "-migration", _binding.ServiceSid, _binding.OwnerSid, _stop.Token)
                ?? Task.Delay(Timeout.Infinite, _stop.Token);
            var history = new ServiceHistoryProtocol(new LogStore().LoadTimelineByBackupId)
                .ServeAsync(_binding.ServiceName + "-history", _binding.ServiceSid, _binding.OwnerSid, _stop.Token);
            var transcripts = new ServiceTranscriptProtocol(AppPaths.EngineLogsRoot, new LogStore().LoadTimelineByBackupId)
                .ServeAsync(_binding, _stop.Token);
            var notifications = new ServiceNotificationProtocol(new(_binding.DataDirectory)).ServeAsync(_binding, _stop.Token);
            var schedule = new ScheduleLoop(new(Path.Combine(_binding.DataDirectory, "schedule-state.json"),
                SchedulePlans, RunScheduledAsync, plan => ScheduleConditions.Blocked(plan, _scheduleDestinations,
                    ScheduleConditions.OnBattery, ScheduleConditions.NetworkAvailable), ScheduleConditions.OnBattery)).RunAsync(_stop.Token);
            var ended = await Task.WhenAny(secrets, commands, handoff, history, transcripts, notifications, schedule).ConfigureAwait(false);
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

    /// <summary>Receives the owner's saved sign-ins once, after setup. The
    /// service keeps them from the moment they are verified.</summary>
    private ServiceMigrationProtocol? Handoff(SecretsStore vault)
    {
        if (_registration.Activated || !Guid.TryParseExact(_registration.MigrationId, "N", out _)) return null;
        var receipt = Path.Combine(_binding.DataDirectory, "migration-" + _registration.MigrationId + ".receipt");
        return new ServiceMigrationProtocol(new(1, _binding.OwnerSid, _registration.MigrationId), receipt, vault,
            beginRefresh: () =>
            {
                if (SystemService.Read(_binding.ServiceName, _binding.OwnerSid)?.Activated == true)
                    throw new IOException("The active service vault cannot be replaced by an earlier desktop copy.");
                return new NoLease();
            },
            accepted: () =>
            {
                SystemService.MarkActivated(_binding.ServiceName);
                AppLogger.For<SystemBackupService>().Information("Saved sign-ins received; the background service keeps them from now on");
            });
    }

    private sealed class NoLease : IDisposable { public void Dispose() { } }

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

    private byte[] DispatchCore(byte[] bytes)
    {
        var request = ServiceHostRequest.Parse(bytes);
        lock (_gate)
        {
            switch (request.Command)
            {
                case "activity":
                    return ServiceActivity.Encode(new ServiceActivity.Snapshot(1, _runId, _state, _backupId, _operation, _startedUtc, _progress));
                case "start" or "start-cli":
                    if (!_registration.Activated && SystemService.Read(_binding.ServiceName, _binding.OwnerSid)?.Activated != true)
                        return Reply("setup-incomplete");
                    if (_returnId is not null) return Reply("removal-pending");
                    if (_worker is not null || _stop.IsCancellationRequested) return Reply("busy");
                    var approved = SystemServiceRuns.Authorize(_installation, request.BackupId!, request.Operation!, ownerCommand: request.Command == "start-cli");
                    StartWorker(request, approved);
                    break;
                case "cancel":
                    if (_worker is null || request.RunId != _runId) return Reply("not-running");
                    RecordRequestedStop();
                    _worker.StandardInput.WriteLine("cancel"); _worker.StandardInput.Flush();
                    _state = "stopping"; SaveStatus();
                    break;
                case "heartbeat":
                    if (_worker is null || request.RunId != _runId)
                        return new ServiceRunJournal(_binding.DataDirectory).ReadFinished(request.RunId!) ?? Reply("not-running");
                    if (!_cliLease.Renew(request.RunId!)) return Reply("lease-expired");
                    break;
                case "freeze":
                    // Turning administrator access off: the saved sign-ins go
                    // back to the owner, so nothing may change them meanwhile.
                    if (_worker is not null) return Reply("busy");
                    _returnId ??= Guid.NewGuid().ToString("N");
                    _returnStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    _ = new ServiceVaultReturnProtocol(_returnId, Path.Combine(_binding.DataDirectory, "return-" + _returnId + ".receipt"), Vault())
                        .ServeAsync(_binding, _returnStop.Token);
                    return JsonSerializer.SerializeToUtf8Bytes(new { version = 1, returnId = _returnId, state = _state });
                case "measure":
                    return _measurements.Start(request.Path!, new SourceSizeRules(request.Filters, request.SkipCloudOnly));
                case "measure-result":
                    return _measurements.Result(request.RunId!);
                case "thaw":
                    _returnStop?.Cancel(); _returnStop = null; _returnId = null;
                    break;
            }
            return Reply(null);
        }
    }

    private void RecordRequestedStop()
    {
        try { _recovery.StopRequested(_runId); }
        catch (Exception ex) { AppLogger.For<SystemBackupService>().Warning(ex, "Stop will proceed although its saved reason could not be updated"); }
    }

    private void StartWorker(ServiceHostRequest request, ServiceRunAuthorization.AuthorizedRun approved)
    {
        if (_recovery.Read() is { Complete: false } prior) _recovery.Recover(prior.HostRunId);
        var start = new ProcessStartInfo(Path.Combine(_installation, "Backspace.exe")) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = _binding.DataDirectory, RedirectStandardInput = true, RedirectStandardOutput = true };
        // stdout is the private framed worker feed, never a console log sink.
        start.Environment["BACKSPACE_DEBUG"] = "0";
        foreach (var argument in new[] { request.Command == "start-cli" ? "--service-worker-cli" : "--service-worker-owned",
            Guid.NewGuid().ToString("N"), request.BackupId!, request.Operation! })
            start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        var started = false;
        _runId = Guid.NewGuid().ToString("N"); _state = "starting"; _result = null;
        _backupId = request.BackupId; _operation = request.Operation; _approved = approved;
        _startedUtc = DateTime.UtcNow; _progress = null; _savedHistoryId = null;
        try
        {
            _recovery.Begin(new(1, _runId, approved.Backup.Id, approved.Backup.Name, approved.Kind,
                _startedUtc.Value, request.Command is "scheduled" or "start-cli", approved.Configuration.Notifications.OnFailure.Toast,
                approved.Configuration.Notifications.OnFailure.Sound));
            SaveStatus();
            if (!process.Start()) throw new IOException("The backup worker could not start.");
            started = true;
            // The child waits for 'start' before touching a repository.
            if (!KillOnExitJobObject.TryAssign(process)) throw new IOException("The worker lifetime could not be protected.");
            _state = "running";
            SaveStatus();
            _worker = process;
            _workerExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (request.Command == "start-cli") _cliLease.Begin(_runId); else _cliLease.Clear();
            process.StandardInput.WriteLine("start"); process.StandardInput.Flush();
            _ = ObserveWorkerAsync(process, ReadProgressAsync(process, request.BackupId!));
        }
        catch
        {
            var stopped = !started;
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); stopped = process.WaitForExit(2000); }
                catch { /* An uncertain child keeps the slot. */ }
            }
            if (stopped) { process.Dispose(); _worker = null; _state = "failed-to-start"; _cliLease.Clear(); }
            else { _worker = process; _state = "unknown"; _ = ObserveWorkerAsync(process, ReadProgressAsync(process, request.BackupId!)); }
            try { SaveStatus(); } catch { /* Preserve the startup failure. */ }
            if (stopped) try { _ = _recovery.Recover(_runId); } catch (Exception ex) { AppLogger.For<SystemBackupService>().Error(ex, "The failed startup remains pending in its recovery record"); }
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
                Process? expired;
                lock (_gate)
                {
                    expired = _cliLease.IsExpired ? _worker : null;
                    if (expired is null) continue;
                    _state = "stopping";
                    try { SaveStatus(); } catch (IOException) { }
                    try { expired.StandardInput.WriteLine("cancel"); expired.StandardInput.Flush(); }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
                }
                try { await expired.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                catch (System.TimeoutException)
                { lock (_gate) if (ReferenceEquals(_worker, expired) && !expired.HasExited) expired.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    // Read until the worker's output ends, also while the service stops: a
    // stopping worker still writes its last progress and the run's record,
    // and with nobody reading, its pipe filled and it hung until the service
    // killed it 15 s later. Quit looked as if it had not worked, and the
    // run's final report was lost (U126, 4 November 2026).
    private async Task ReadProgressAsync(Process process, string backupId)
    {
        try
        {
            while (true)
            {
                var bytes = await BackupServicePipe.ReadMessageAsync(process.StandardOutput.BaseStream, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
                var progress = ServiceActivity.ParseProgress(bytes, backupId);
                lock (_gate) if (ReferenceEquals(process, _worker))
                {
                    _progress = progress;
                    if (progress.LogRunId is { } historyId && _runId is not null && _savedHistoryId != historyId)
                    {
                        try { _recovery.TrackHistory(_runId, historyId); _savedHistoryId = historyId; }
                        catch (Exception ex) { AppLogger.For<SystemBackupService>().Warning(ex, "The live run identity could not yet be retained"); }
                    }
                }
            }
        }
        catch (EndOfStreamException) { }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { AppLogger.For<SystemBackupService>().Warning(ex, "Live worker activity could not be read"); }
    }

    private async Task ObserveWorkerAsync(Process process, Task progress)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        try { await progress.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (System.TimeoutException) { }
        lock (_gate)
        {
            _result = process.ExitCode; _state = _result == 0 ? "completed" : "failed";
            try
            {
                if (_progress?.Result is { } known && _runId is not null) { _recovery.TrackHistory(_runId, known.Id); _recovery.Confirm(_runId); }
                // A check reports no live result, but saves its own history:
                // a clean exit is that result, not an interruption (lab VM,
                // 2 November 2026: a successful check also showed "stopped unexpectedly").
                else if (process.ExitCode == 0 && _runId is not null && SavedOutcome() is { } saved)
                {
                    _recovery.TrackHistory(_runId, saved.Id); _recovery.Confirm(_runId);
                    _progress = new(saved.BackupId, 0, "", [], [], saved, LogRunId: saved.Id);
                }
                else if (_recovery.Recover(_runId, FailureReason()) is { } recovered && _backupId is not null)
                    _progress = new(_backupId, 0, "", [], [], recovered, LogRunId: recovered.Id);
            }
            catch (Exception ex) { AppLogger.For<SystemBackupService>().Error(ex, "The interrupted worker outcome remains pending for recovery"); }
            try { SaveStatus(); } catch (Exception ex) { AppLogger.For<SystemBackupService>().Error(ex, "Could not save service worker exit"); }
            _worker = null; _cliLease.Clear(); process.Dispose();
            _workerExited?.TrySetResult(); _workerExited = null;
        }
    }

    private RunRecord? SavedOutcome()
    {
        if (_backupId is null || _startedUtc is not { } started || _approved is not { } run) return null;
        try
        {
            return new LogStore(Path.Combine(_binding.DataDirectory, "logs", "engine")).LoadTimelineByBackupId(_backupId)
                .Where(r => r.Kind == run.Kind && r.StartedUtc >= started.AddSeconds(-5) && r.EndedUtc is not null
                    && r.Status != BackupRunStatus.Running && Guid.TryParseExact(r.Id, "N", out _))
                .OrderBy(r => r.StartedUtc).FirstOrDefault();
        }
        catch (Exception ex) { AppLogger.For<SystemBackupService>().Warning(ex, "Could not read the finished run's history"); return null; }
    }

    private string? FailureReason()
    {
        try
        {
            var path = Path.Combine(_binding.DataDirectory, ServiceNetworkShares.ReasonFile);
            if (File.Exists(path) && new FileInfo(path).Length is > 0 and < 2048)
            {
                var reason = File.ReadAllText(path).Trim();
                File.Delete(path);
                if (reason.Length > 0) return reason;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { return _approved is { } run ? ServiceApprovedWorker.MissingDestination(run) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The owner's backups as saved, re-read every minute; null while
    /// setup or removal is unfinished or the settings cannot be read.</summary>
    private System.Collections.Generic.IReadOnlyList<SchedulePlan>? SchedulePlans()
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
        lock (_gate)
        {
            if (_worker is not null || _stop.IsCancellationRequested || _returnId is not null) return false;
            var operation = plan.Kind.ToString().ToLowerInvariant();
            var approved = SystemServiceRuns.Authorize(_installation, plan.Backup.Id, operation, ownerCommand: false);
            StartWorker(new ServiceHostRequest(1, "scheduled", null, plan.Backup.Id, operation), approved);
            exited = _workerExited?.Task ?? Task.CompletedTask;
        }
        // A time limit or battery stops the worker, then waits for it.
        using (ct.Register(() => { lock (_gate) try { _worker?.StandardInput.WriteLine("cancel"); _worker?.StandardInput.Flush(); } catch (Exception) { } }))
            await exited.ConfigureAwait(false);
        return true;
    }

    private byte[] Reply(string? error) => JsonSerializer.SerializeToUtf8Bytes(new { version = 1, runId = _runId, state = _state, exitCode = _result, error });
    private void SaveStatus() => new ServiceRunJournal(_binding.DataDirectory).Save(Reply(null));

    protected override void OnStop()
    {
        _stop.Cancel();
        Process? child;
        lock (_gate)
        {
            child = _worker;
            if (child is not null)
            {
                RecordRequestedStop();
                try { child.StandardInput.WriteLine("cancel"); child.StandardInput.Flush(); } catch (IOException) { }
            }
        }
        if (child is not null)
        {
            RequestAdditionalTime(20000);
            try { if (!child.WaitForExit(15000)) child.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
        try { _lifetime?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        try { _leaseMonitor?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        AppLogger.For<SystemBackupService>().Information("The background service stopped");
    }

    protected override void OnShutdown() => OnStop();
}
