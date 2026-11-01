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
            try { await Console.In.ReadLineAsync().ConfigureAwait(false); stop.Cancel(); }
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
        if (!binding.CanRun) throw new SecurityException("Credential handoff and service setup must finish before a backup starts.");
        // Inspect every shipped dependency, not just the apphost. The installed
        // directory and all ancestors must prevent replacement by an ordinary user.
        ServiceInstallationSecurity.ValidateCodeTree(installation, binding.DataDirectory, binding.ServiceSid);
        foreach (var binary in new[] { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe" })
            ServiceInstallationSecurity.ValidateCodeFile(Path.Combine(installation, binary), installation);
        var policy = ServiceRunAuthorization.Load(ServicePortableLayout.ExistingRecord(installation, "service-approval.json"), installation, binding.OwnerSid);
        var run = policy.Authorize(JsonSerializer.SerializeToUtf8Bytes(
            new ServiceRunAuthorization.Request(1, revision, backupId, operation), ServiceRunAuthorization.Json), ownerCommand);

        // Serialize workers across sessions using a private file, not the
        // interactive user's Local\\ mutex namespace. No automatic replay.
        using var exclusive = new FileStream(Path.Combine(binding.DataDirectory, "worker.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var vault = new SecretsStore(new DpapiSecretsEncryption(), Path.Combine(binding.DataDirectory, "secrets.bin"));
        vault.WithMigrationSnapshot(_ => true, requireExisting: true);
        ServiceWorkerPrivileges.EnableExistingBackupRights();
        // A connected external drive the service cannot write yet is prepared
        // by the settings task first (U061).
        ServiceExternalDrives.EnsureAccess(binding, run.Backup, run.Configuration, TimeSpan.FromMinutes(2), stop);
        using var paths = ServicePathLease.Acquire(LocalPathsFor(run), stop);
        // These are this operation's own temporary guard files, not user data.
        // Keep them out of snapshots, including VSS snapshots taken while held.
        foreach (var approvedBackup in run.Configuration.Backups)
            approvedBackup.FiltersText += "\n" + string.Join("\n", paths.MarkerNames);

        // All process-local state and engine scratch belongs to the service.
        // A caller's inherited portable-config/TEMP overrides cannot redirect it.
        Environment.SetEnvironmentVariable("BACKSPACE_CONFIG_ROOT", binding.DataDirectory);
        var scratch = Path.Combine(binding.DataDirectory, "scratch");
        Directory.CreateDirectory(scratch);
        foreach (var key in new[] { "TEMP", "TMP", "TMPDIR" }) Environment.SetEnvironmentVariable(key, scratch);
        Environment.SetEnvironmentVariable("BACKSPACE_NO_UPDATE_CHECK", "1");
        Environment.CurrentDirectory = binding.DataDirectory;
        HasVerifiedVssIdentity = true;
        try
        {
            ServiceLocator.InitializeCore(null, vault, new WorkerScheduler());
            if (!ServiceLocator.Config.IsLoaded) throw new IOException("The service settings could not be read. No operation was started.");
            ServiceLocator.Config.ApplyServiceApproval(run.Configuration);
            var notifications = new ServiceNotificationQueue(binding.DataDirectory);
            ServiceLocator.Notifier.DurableDelivery = notice => { notifications.Add(notice); return Task.CompletedTask; };
            var backup = ServiceLocator.Config.Current.Backups.Single(b => b.Id == run.Backup.Id);
            await using var activity = activityOutput is not null
                ? new ServiceWorkerActivity(activityOutput, backupId, ServiceLocator.Progress, ServiceLocator.Orchestrator, ServiceLocator.Engine) : null;
            var result = run.Kind switch
            {
                RunKind.Backup => await ServiceLocator.Orchestrator.RunBackupAsync(backup, stop).ConfigureAwait(false),
                RunKind.Check => await ServiceLocator.Orchestrator.RunCheckAsync(backup, stop).ConfigureAwait(false),
                RunKind.Prune => await ServiceLocator.Orchestrator.RunPruneAsync(backup, stop).ConfigureAwait(false),
                _ => throw new SecurityException("Unapproved operation."),
            };
            await ServiceLocator.BackrestSidecar.DisposeAsync().ConfigureAwait(false);
            ServiceLocator.Maintenance.ReleaseEngineOwnershipAfterStop();
            var report = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, approvalRevision = run.Revision, serviceSid = binding.ServiceSid,
                ownerSid = binding.OwnerSid, backupId, operation, finishedUtc = DateTime.UtcNow,
                vaultReadVerified = true, administrator = false, run = result,
            }, ServiceRunAuthorization.Json);
            var reportPath = Path.Combine(binding.DataDirectory, "last-service-run.json");
            using (var file = new FileStream(reportPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(report); file.Flush(flushToDisk: true); }
            File.Move(reportPath + ".tmp", reportPath, overwrite: true);
            return result.Status == BackupRunStatus.Success ? 0 : 4;
        }
        finally
        {
            if (ServiceLocator.Initialized)
            {
                await ServiceLocator.BackrestSidecar.DisposeAsync().ConfigureAwait(false);
                ServiceLocator.Updates.Dispose();
            }
            HasVerifiedVssIdentity = false;
            AppLogger.Shutdown();
        }
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

    internal static IEnumerable<string> LocalPathsFor(ServiceRunAuthorization.AuthorizedRun run)
    {
        // Repository maintenance does not read the original source. Only an
        // explicitly requested comparison with today's originals needs those
        // paths during a check; ordinary test restores use repository hashes.
        if (run.Kind == RunKind.Backup || (run.Kind == RunKind.Check
            && run.Backup.CheckRestoreFiles && run.Backup.RestoreCheckOptions.CompareOriginals))
            foreach (var path in run.Backup.SourcePaths) yield return ServiceInstallationSecurity.CanonicalLocalPath(path);
        foreach (var target in run.Backup.Targets)
        {
            var destination = run.Configuration.Destinations.Single(d => d.Id == target.DestinationId);
            if (destination.Kind == DestinationKind.NetworkShare)
                throw new NotSupportedException("Backups to a network share run from the tray app, not the background service.");
            // An unplugged drive is left out; its part of the run is skipped.
            if (destination.Kind == DestinationKind.ExternalDrive && ServiceExternalDrives.ConnectedPath(destination) is { } external)
                yield return external;
            if (destination.Kind == DestinationKind.LocalFolder)
                yield return ServiceInstallationSecurity.CanonicalLocalPath(destination.PathOrSubpath);
        }
    }

    private sealed class WorkerScheduler : IScheduler
    {
        public void UpsertTask(Backup backup, string exePath) => throw new InvalidOperationException("A service worker cannot register user tasks.");
        public void DeleteTask(Backup backup) => throw new InvalidOperationException("A service worker cannot delete user tasks.");
        public string TaskNameFor(Backup backup) => "Service: " + backup.Id;
    }
}
