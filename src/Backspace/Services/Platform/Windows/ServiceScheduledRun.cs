using System;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>Run now, Save and run, and the owner's command line, sent to the
/// background service. The client starts the approved run, then keeps it
/// alive with heartbeats and follows it to its result; the backup runs in the
/// restricted service account. The service runs its own schedule itself.</summary>
internal static class ServiceScheduledRun
{
    internal sealed record Response(int Version, string? RunId, string State, int? ExitCode, string? Error);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(8);

    internal static async Task<int> RunApprovedAsync(string installation, ServiceWorkerBinding binding,
        string revision, string backupId, string operation, CancellationToken cancellation, Action<string>? status = null)
    {
        ServiceRetirement.RequireNotRetiring(installation);
        ServiceProgramUpdate.RequireNoPending(installation);
        ServiceRegistration.VerifyInstalled(binding, Path.Combine(installation, "Backspace.exe"));
        var request = new ServiceHostRequest(1, "start-cli", revision, backupId, operation);
        ServiceHostRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json));
        var approval = ServiceRunAuthorization.Load(ServicePortableLayout.ExistingRecord(installation, "service-approval.json"), installation, binding.OwnerSid);
        var run = approval.Authorize(JsonSerializer.SerializeToUtf8Bytes(
            new ServiceRunAuthorization.Request(1, revision, backupId, operation), ServiceRunAuthorization.Json), ownerCommand: true);
        ServiceConfigurationComparison.RequireCurrent(installation, run.Configuration);
        var schedule = operation switch
        {
            "check" => run.Backup.CheckSchedule,
            "prune" => run.Backup.PruneSchedule,
            _ => run.Backup.Schedule,
        };
        if (schedule?.PausedUntilUtc > DateTime.UtcNow)
        { status?.Invoke($"Paused until {schedule.PausedUntilUtc:O}. Nothing was started."); return 0; }
        using var controller = new ServiceController(binding.ServiceName);
        ServiceRegistration.EnsureRunning(controller, TimeSpan.FromSeconds(20));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        return await RunAsync(request, async (message, token) =>
        {
            if (message.Command == "start-cli") ServiceConfigurationComparison.RequireCurrent(installation, run.Configuration);
            using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-runs",
                binding.ServiceSid, ExchangeTimeout, token).ConfigureAwait(false);
            await BackupServicePipe.WriteMessageAsync(pipe,
                JsonSerializer.SerializeToUtf8Bytes(message, ServiceRunAuthorization.Json), ExchangeTimeout, token).ConfigureAwait(false);
            var bytes = await BackupServicePipe.ReadResponseAsync(pipe, ExchangeTimeout, token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Response>(bytes, ServiceRunAuthorization.Json)
                ?? throw new InvalidDataException("The backup service returned an empty status.");
        }, delay: token => Task.Delay(PollInterval(elapsed.Elapsed), token), cancellation, outcome => status?.Invoke(
            outcome.State == "paused" ? "Automatic backups are paused for this Windows session. Nothing was started."
            : outcome.ExitCode == 0 ? "Command finished. See Activity & logs for the backup result."
            : "The operation did not finish successfully. See Activity & logs for details.")).ConfigureAwait(false);
    }

    internal static TimeSpan PollInterval(TimeSpan elapsed) => elapsed < TimeSpan.FromSeconds(15)
        ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync(ServiceHostRequest start,
        Func<ServiceHostRequest, CancellationToken, Task<Response>> exchange,
        Func<CancellationToken, Task> delay, CancellationToken cancellation, Action<Response>? finishedResponse = null)
    {
        string? runId = null;
        var finished = false;
        try
        {
            var response = await exchange(start, cancellation).ConfigureAwait(false);
            // Only an explicit refusal is safe to retry. A missing start reply
            // is uncertain and must never launch a duplicate operation.
            while (response.Version == 1 && response.Error == "busy")
            {
                await delay(cancellation).ConfigureAwait(false);
                response = await exchange(start, cancellation).ConfigureAwait(false);
            }
            // A paused schedule did not start a worker. No guessed identity,
            // heartbeat or cancellation is sent for the preceding run.
            if (response is { Version: 1, State: "paused", RunId: null, ExitCode: 0, Error: null })
            { finishedResponse?.Invoke(response); return 0; }
            if (response.Version != 1 || response.Error is not null
                || !Guid.TryParseExact(response.RunId, "N", out _))
                throw new IOException("The backup service did not accept the scheduled run.");
            runId = response.RunId;
            while (true)
            {
                if (response.Version != 1 || response.RunId != runId)
                    throw new IOException("The scheduled backup's result could not be confirmed.");
                if (response.State is "completed" or "failed")
                {
                    if (response.ExitCode is null || (response.State == "completed") != (response.ExitCode == 0)
                        || response.Error is not (null or "not-running"))
                        throw new IOException("The backup service returned an inconsistent result.");
                    finished = true;
                    finishedResponse?.Invoke(response);
                    return response.ExitCode.Value;
                }
                if (response.Error is not null || response.State is not ("running" or "stopping"))
                    throw new IOException("The scheduled backup could not continue.");
                await delay(cancellation).ConfigureAwait(false);
                response = await exchange(new ServiceHostRequest(1, "heartbeat", RunId: runId), cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            if (runId is not null && !finished)
            {
                // Never cancel an unknown/newer run or retry a start whose
                // reply was lost. A hard-killed dispatcher is handled by the lease.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await exchange(new ServiceHostRequest(1, "cancel", RunId: runId), timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SecurityException) { }
            }
        }
    }
}
