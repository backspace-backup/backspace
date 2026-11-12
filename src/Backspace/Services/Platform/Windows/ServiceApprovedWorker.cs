using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Runs one approved operation through the ordinary orchestrator as a
/// dedicated service account. No installer or desktop activation is implied by
/// this entry point. It cannot accept source paths, secrets or commands.</summary>
internal static class ServiceApprovedWorker
{
    internal static bool HasVerifiedVssIdentity { get; private set; }

    /// <summary>Set by the service on the worker it starts: "1" when its own
    /// schedule started the run (U173).</summary>
    internal const string AutomaticRunVariable = "BACKSPACE_RUN_AUTOMATIC";

    /// <summary>Set by the service on a worker its schedule starts for one
    /// check entry (U179); empty runs every check of the backup.</summary>
    internal const string CheckIdVariable = "BACKSPACE_CHECK_ID";

    /// <summary>The check a worker runs: the schedule's entry, every check
    /// (null) for the owner's command, or none when the entry was removed.</summary>
    internal static (bool Found, BackupCheck? Check) CheckToRun(Backup backup, bool ownerCommand, string? variable)
    {
        if (ownerCommand || string.IsNullOrEmpty(variable)) return (true, null);
        var check = backup.Checks.FirstOrDefault(c => c.Id == variable && c.Schedule is not null);
        return (check is not null, check);
    }

    /// <summary>Whether this worker's run was started by the service's
    /// schedule: never for the owner's command (Run now, command line).</summary>
    internal static bool StartedBySchedule(bool ownerCommand, string? variable) => !ownerCommand && variable == "1";

    internal static async Task<int> RunOwnedAsync(string revision, string backupId, string operation, bool ownerCommand = false)
    {
        var binding = ServiceWorkerBinding.Load(ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\')));
        binding.ValidateWorkerIdentity();
        using var stop = new CancellationTokenSource();
        // No engine starts before the SCM host has attached this process tree
        // to its lifetime job. EOF also cancels if the parent disappears.
        var start = await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (start != "start") throw new SecurityException("The service did not admit this worker.");
        var monitor = Task.Run(async () =>
        {
            try
            {
                // "interrupt": the service is stopping and resumes the backup
                // later, so it is recorded as interrupted, not stopped (U131).
                if (await Console.In.ReadLineAsync().ConfigureAwait(false) == "interrupt")
                    try { ServiceLocator.Orchestrator.CancelRun(backupId, "Interrupted"); }
                    catch (Exception) { /* Not running yet: stopping is enough. */ }
                stop.Cancel();
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        });
        return await RunAsync(revision, backupId, operation, stop.Token, Console.OpenStandardOutput(), ownerCommand).ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(string revision, string backupId, string operation, CancellationToken stop, Stream? activityOutput = null, bool ownerCommand = false)
    {
        var installation = ServiceInstallationSecurity.CanonicalLocalPath(AppContext.BaseDirectory.TrimEnd('\\'));
        var binding = ServiceWorkerBinding.Load(installation);
        binding.ValidateWorkerIdentity();
        if (!binding.CanRun) throw new SecurityException("Service setup must finish (open Backspace on this computer) before a backup starts.");
        // The owner's settings as saved, read now (U100).
        var run = SystemServiceRuns.Authorize(installation, backupId, operation, ownerCommand);

        // Several workers run at once (U156): how many back up together is
        // up to the destination and total limits (RepositoryAccess), which
        // also say "Queued" while one waits. Each worker runs its own restic
        // (U175) and has its own reason file; the host passes its run
        // identity as the revision.
        var reasonPath = Path.Combine(binding.DataDirectory, ServiceNetworkShares.ReasonFileFor(revision));
        try { File.Delete(reasonPath); } catch (IOException) { }
        var vault = OpenVault(binding, installation, reasonPath);
        ServiceWorkerPrivileges.EnableExistingBackupRights();
        IDisposable shares;
        try { shares = ServiceNetworkShares.Connect(run.Backup, run.Configuration, vault); }
        catch (ShareConnectionException ex)
        {
            // The host explains the failed run with this (FailureReason).
            File.WriteAllText(reasonPath, ex.Message);
            throw;
        }
        using var _shares = shares;
        // The service trusts the owner's folders as the owner does: no guard
        // files are placed in them, and a missing source is reported by the run
        // itself instead of stopping the worker (lab VM, 2 November 2026).

        // All process-local state and engine scratch belongs to the service.
        // A caller's inherited portable-config/TEMP overrides cannot redirect it.
        Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", binding.DataDirectory);
        // Engine scratch is on this computer, beside its other working files,
        // never in the synced portable folder (U153).
        var scratch = EngineWorkPaths.ScratchDir;
        Directory.CreateDirectory(scratch);
        foreach (var key in new[] { "TEMP", "TMP", "TMPDIR" }) Environment.SetEnvironmentVariable(key, scratch);
        Environment.SetEnvironmentVariable("BACKSPACE_NO_UPDATE_CHECK", "1");
        Environment.CurrentDirectory = binding.DataDirectory;
        HasVerifiedVssIdentity = true;
        try
        {
            ServiceLocator.InitializeCore(vault, new WorkerScheduler());
            if (!ServiceLocator.Config.IsLoaded) throw new IOException("The service settings could not be read. No operation was started.");
            ServiceLocator.Config.ApplyServiceApproval(run.Configuration);
            var notifications = new ServiceNotificationQueue(binding.DataDirectory);
            ServiceLocator.Notifier.DurableDelivery = notice => { notifications.Add(notice); return Task.CompletedTask; };
            var backup = ServiceLocator.Config.Current.Backups.Single(b => b.Id == run.Backup.Id);
            await using var activity = activityOutput is not null
                ? new ServiceWorkerActivity(activityOutput, backupId, ServiceLocator.Progress, ServiceLocator.Orchestrator, ServiceLocator.Engine) : null;
            var result = run.Kind switch
            {
                RunKind.Backup => await ServiceLocator.Orchestrator.RunBackupAsync(backup, stop,
                    automatic: StartedBySchedule(ownerCommand, Environment.GetEnvironmentVariable(AutomaticRunVariable))).ConfigureAwait(false),
                RunKind.Check => CheckToRun(backup, ownerCommand, Environment.GetEnvironmentVariable(CheckIdVariable)) switch
                {
                    (true, { } check) => await ServiceLocator.Orchestrator.RunCheckAsync(backup, check, stop).ConfigureAwait(false),
                    (true, null) => await ServiceLocator.Orchestrator.RunCheckAsync(backup, stop).ConfigureAwait(false),
                    _ => throw new InvalidDataException("This check was removed from the backup before it could start."),
                },
                RunKind.Prune => await ServiceLocator.Orchestrator.RunPruneAsync(backup, stop).ConfigureAwait(false),
                _ => throw new SecurityException("Unapproved operation."),
            };
            ServiceLocator.Maintenance.ReleaseEngineOwnershipAfterStop();
            var report = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, approvalRevision = run.Revision, serviceSid = binding.ServiceSid,
                ownerSid = binding.OwnerSid, backupId, operation, finishedUtc = DateTime.UtcNow,
                vaultReadVerified = true, vaultId = vault.VaultId, administrator = false, run = result,
            }, ServiceRunAuthorization.Json);
            var reportPath = Path.Combine(binding.DataDirectory, "last-service-run.json");
            // A fresh temporary name, and no limit on waiting for another
            // program to let go (U135).
            ServiceNotificationQueue.WriteRecord(reportPath, report);
            return result.Status == BackupRunStatus.Success ? 0 : 4;
        }
        finally
        {
            if (ServiceLocator.Initialized) ServiceLocator.Updates.Dispose();
            HasVerifiedVssIdentity = false;
            AppLogger.Shutdown();
        }
    }

    /// <summary>The credentials this run uses. Version 3 opens the portable
    /// vault with the service's own key copy (U153); when it can't, the run
    /// stops before any engine starts and the reason goes into its history.</summary>
    private static SecretsStore OpenVault(ServiceWorkerBinding binding, string installation, string reasonPath)
    {
        var vault = ServiceVaultUnlock.ServiceVault(installation, EngineWorkPaths.MachineRoot);
        string? reason = vault.Refresh() switch
        {
            VaultState.Locked => VaultLockedException.LockedMessage,
            VaultState.Unreadable => vault.LoadErrorMessage ?? "Backspace can't open its saved passwords. Open Backspace on this computer to see why.",
            _ => null,
        };
        if (reason is null) return vault;
        FileAccessRetry.Run(() => File.WriteAllText(reasonPath, reason), reasonPath);
        throw new VaultLockedException(vault.State, reason);
    }

    /// <summary>Why a worker that ended without a result could not run, when
    /// the reason is a destination folder that is gone (renamed, deleted, or on
    /// a drive that is not connected). The worker stops before its engine starts
    /// in that case, so this is the only account of it (U051).</summary>
    internal static string? MissingDestination(ServiceRunAuthorization.AuthorizedRun run)
    {
        foreach (var target in run.Backup.Targets)
        {
            var destination = run.Configuration.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
            if (destination is { Kind: DestinationKind.ExternalDrive } && ServiceExternalDrives.ConnectedPath(destination) is { } external)
            {
                if (!Directory.Exists(external))
                    return $"The backup folder of «{destination.Name}» is missing on the drive ({external}). Choose another folder for this destination, or create it.";
                if (!ServiceExternalDrives.CanWrite(external))
                    return $"Backspace's background service cannot write to «{destination.Name}» ({external}). Open Backspace and save the backup's settings once to give it access.";
                continue;
            }
            if (destination is not { Kind: DestinationKind.LocalFolder } || string.IsNullOrWhiteSpace(destination.PathOrSubpath)) continue;
            if (!Directory.Exists(destination.PathOrSubpath))
                return $"The backup folder of «{destination.Name}» is missing ({destination.PathOrSubpath}). Reconnect the drive it is on, or choose another folder for this destination.";
        }
        return null;
    }

    private sealed class WorkerScheduler : IScheduler
    {
        public void UpsertTask(Backup backup, string exePath) => throw new InvalidOperationException("A service worker cannot register user tasks.");
        public void DeleteTask(Backup backup) => throw new InvalidOperationException("A service worker cannot delete user tasks.");
        public string TaskNameFor(Backup backup) => "Service: " + backup.Id;
    }
}
