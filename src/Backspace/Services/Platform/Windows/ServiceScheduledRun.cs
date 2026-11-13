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
/// service. The service runs its own schedule itself.</summary>
internal static class ServiceScheduledRun
{
    internal sealed record Response(int Version, string? RunId, string State, int? ExitCode, string? Error);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    internal static async Task<int> RunApprovedAsync(string installation, ServiceWorkerBinding binding,
        string revision, string backupId, string operation, CancellationToken cancellation, Action<string>? status = null,
        Action<string>? queued = null, bool commandLine = false, bool waiveConditions = false)
    {
        // The service runs the settings as saved (U100); the revision is unused.
        revision = Guid.NewGuid().ToString("N");
        var run = SystemServiceRuns.Authorize(installation, backupId, operation, ownerCommand: true);
        var request = new ServiceHostRequest(1, "start-cli", revision, backupId, operation, WaiveConditions: waiveConditions);
        ServiceHostRequest.Parse(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json));
        var schedule = operation switch
        {
            "check" => null, // Checks are not paused on their own (U179).
            "prune" => run.Backup.PruneSchedule,
            _ => run.Backup.Schedule,
        };
        if (schedule?.PausedUntilUtc > DateTime.UtcNow)
        { status?.Invoke($"Paused until {schedule.PausedUntilUtc:O}. Nothing was started."); return 0; }
        using var controller = new ServiceController(binding.ServiceName);
        // The owner's command line runs even after Quit, without bringing the
        // schedule back (U180); Run now comes from an open Backspace.
        ServiceRegistration.EnsureRunning(controller, StartTimeout, commandLine ? [SystemServiceLifetime.ForCommand] : []);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        // The command line prints what the run does (U180).
        var transcript = commandLine && status is not null ? new CommandLineTranscript(backupId, DateTime.UtcNow, status) : null;
        async Task ShowProgressAsync(bool finished, CancellationToken token)
        {
            if (transcript is null) return;
            try
            {
                var activity = await ServiceActivity.ReadAsync(binding, token).ConfigureAwait(false);
                transcript.Show(activity);
                if (finished) transcript.ShowSummary(activity);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            { AppLogger.For(nameof(ServiceScheduledRun)).Debug(ex, "The command line could not read the run's progress"); }
        }
        string? finishedLine = null;
        var code = await RunAsync(request, async (message, token) =>
        {
            using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-runs",
                binding.ServiceSid, ExchangeTimeout, token).ConfigureAwait(false);
            await BackupServicePipe.WriteMessageAsync(pipe,
                JsonSerializer.SerializeToUtf8Bytes(message, ServiceRunAuthorization.Json), ExchangeTimeout, token).ConfigureAwait(false);
            var bytes = await BackupServicePipe.ReadResponseAsync(pipe, ExchangeTimeout, token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Response>(bytes, ServiceRunAuthorization.Json)
                ?? throw new InvalidDataException("The backup service returned an empty status.");
        }, delay: async token => { await Task.Delay(PollInterval(elapsed.Elapsed), token).ConfigureAwait(false); await ShowProgressAsync(false, token).ConfigureAwait(false); },
            cancellation, outcome => finishedLine = (
            outcome.State == "paused" ? "Automatic backups are paused for this Windows session. Nothing was started."
            : outcome.ExitCode == 0 ? $"Command finished. See Activity & logs for the {ServiceCommandLineRun.Noun(operation)} result."
            : "The operation did not finish successfully. See Activity & logs for details."), queued).ConfigureAwait(false);
        // What it did, then how it ended.
        await ShowProgressAsync(true, CancellationToken.None).ConfigureAwait(false);
        if (finishedLine is not null) status?.Invoke(finishedLine);
        return code;
    }

    /// <summary>Shown while the service can't start the run yet.</summary>
    internal const string BusyReason = "waiting for the background service to start it";

    /// <summary>Once a second while it starts, then every five (U224: not four
    /// times a second as before).</summary>
    internal static TimeSpan PollInterval(TimeSpan elapsed) => elapsed < TimeSpan.FromSeconds(15)
        ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync(ServiceHostRequest start,
        Func<ServiceHostRequest, CancellationToken, Task<Response>> exchange,
        Func<CancellationToken, Task> delay, CancellationToken cancellation, Action<Response>? finishedResponse = null,
        Action<string>? queued = null, bool commandLine = false)
    {
        string? runId = null;
        var finished = false;
        try
        {
            var response = await exchange(start, cancellation).ConfigureAwait(false);
            // Only an explicit refusal is safe to retry. A missing start reply
            // is uncertain and must never launch a duplicate operation.
            // Busy: this backup already runs in the service, or the service
            // has as many workers as it starts at once. The card says so
            // instead of "Running" (U156).
            var waited = false;
            while (response.Version == 1 && response.Error == "busy")
            {
                if (!waited) { waited = true; queued?.Invoke(BusyReason); }
                await delay(cancellation).ConfigureAwait(false);
                response = await exchange(start, cancellation).ConfigureAwait(false);
            }
            if (waited) queued?.Invoke("");
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
