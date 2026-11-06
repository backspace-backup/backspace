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
        if (!binding.CanRun) throw new SecurityException("Credential handoff and service setup must finish before a backup starts.");
        ServiceRunAuthorization.AuthorizedRun run;
        if (binding.IsSystem)
        {
            // Version 3 (U100): the owner's settings as saved, read now.
            run = SystemServiceRuns.Authorize(installation, backupId, operation, ownerCommand);
        }
        else
        {
            // Inspect every shipped dependency, not just the apphost. The installed
            // directory and all ancestors must prevent replacement by an ordinary user.
            ServiceInstallationSecurity.ValidateCodeTree(installation, binding.DataDirectory, binding.ServiceSid);
            foreach (var binary in new[] { "Backspace.exe", "backrest.exe", "restic.exe", "rclone.exe" })
                ServiceInstallationSecurity.ValidateCodeFile(Path.Combine(installation, binary), installation);
            var policy = ServiceRunAuthorization.Load(ServicePortableLayout.ExistingRecord(installation, "service-approval.json"), installation, binding.OwnerSid);
            run = policy.Authorize(JsonSerializer.SerializeToUtf8Bytes(
                new ServiceRunAuthorization.Request(1, revision, backupId, operation), ServiceRunAuthorization.Json), ownerCommand);
        }

        // Serialize workers across sessions using a private file, not the
        // interactive user's Local\\ mutex namespace. No automatic replay.
        // A worker still exiting, or Dropbox or a virus scanner opening the
        // file, is waited out (U135).
        var workerLock = Path.Combine(binding.DataDirectory, "worker.lock");
        using var exclusive = FileAccessRetry.Get(() => new FileStream(workerLock,
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), workerLock, ct: stop);
        var vault = new SecretsStore(new DpapiSecretsEncryption(), Path.Combine(binding.DataDirectory, "secrets.bin"));
        vault.WithMigrationSnapshot(_ => true, requireExisting: true);
        ServiceWorkerPrivileges.EnableExistingBackupRights();
        // A connected external drive the service cannot write yet is prepared
        // by the settings task first (U061).
        // Windows' own account can already write there (version 3).
        if (!binding.IsSystem) ServiceExternalDrives.EnsureAccess(binding, run.Backup, run.Configuration, TimeSpan.FromMinutes(2), stop);
        var reasonPath = Path.Combine(binding.DataDirectory, ServiceNetworkShares.ReasonFile);
        try { File.Delete(reasonPath); } catch (IOException) { }
        IDisposable shares;
        try { shares = ServiceNetworkShares.Connect(run.Backup, run.Configuration, vault); }
        catch (ShareConnectionException ex)
        {
            // The host explains the failed run with this (FailureReason).
            File.WriteAllText(reasonPath, ex.Message);
            throw;
        }
        using var _shares = shares;
        // Version 3 (U100) trusts the owner's folders as the owner does: no
        // guard files are placed in them, and a missing source is reported by
        // the run itself instead of stopping the worker (lab VM, 2 November 2026).
        using var paths = binding.IsSystem ? null : ServicePathLease.Acquire(LocalPathsFor(run), stop);
        // Its backup folders on this PC are another matter: other accounts may
        // be able to change one (an external drive lets every account write by
        // default). A subfolder swapped for a link would turn the engine's
        // deletes, made as Windows itself, onto any file (U152). Each of the
        // repository's folders is held, with a guard file that keeps it from
        // being emptied and turned into a link, for the whole operation; a
        // link found already in place stops the run. Restic ignores the files.
        using var repositories = binding.IsSystem ? ServicePathLease.Acquire(RepositoryFoldersFor(run), stop) : null;
        // These are this operation's own temporary guard files, not user data.
        // Keep them out of snapshots, including VSS snapshots taken while held.
        foreach (var lease in new[] { paths, repositories })
            if (lease is not null)
                foreach (var approvedBackup in run.Configuration.Backups)
                    approvedBackup.FiltersText += "\n" + string.Join("\n", lease.MarkerNames);

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
            // A fresh temporary name, and no limit on waiting for another
            // program to let go (U135).
            ServiceNotificationQueue.WriteRecord(reportPath, report);
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

    /// <summary>The folders of this run's repositories on this PC: each
    /// repository's own folder and the folders restic keeps in it, or, before
    /// the first backup, the nearest folder that exists on the way to it.</summary>
    internal static IEnumerable<string> RepositoryFoldersFor(ServiceRunAuthorization.AuthorizedRun run)
    {
        foreach (var target in run.Backup.Targets)
        {
            var destination = run.Configuration.Destinations.Single(d => d.Id == target.DestinationId);
            string? repository = destination.Kind switch
            {
                DestinationKind.ExternalDrive => ServiceExternalDrives.ConnectedPath(destination),
                DestinationKind.LocalFolder => ServiceInstallationSecurity.CanonicalLocalPath(destination.PathOrSubpath),
                _ => null,
            };
            if (repository is null) continue;
            foreach (var folder in RepositoryFolders(repository)) yield return folder;
        }
    }

    internal static IEnumerable<string> RepositoryFolders(string repository)
    {
        // Before the first backup the folder is made here, so that it is held
        // like the rest: holding only the folder above it would still let
        // another account put a link at its name before restic creates it
        // (U152). An empty folder is not a repository: the drive check still
        // treats it as missing.
        if (!Directory.Exists(repository))
        {
            Directory.CreateDirectory(repository);
            yield return repository;
            yield break;
        }
        yield return repository;
        foreach (var name in new[] { "index", "keys", "locks", "snapshots", "data" })
        {
            var folder = Path.Combine(repository, name);
            if (!Directory.Exists(folder)) continue;
            yield return folder;
            if (name == "data")
                foreach (var pack in Directory.EnumerateDirectories(folder)) yield return pack;
        }
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
            // A network share has no local path to hold; the worker connects to
            // it with its saved credentials (U071). Without them it stays with
            // the tray app and is never approved for the service.
            if (destination.Kind == DestinationKind.NetworkShare)
            {
                if (!NetworkSharePath.ServiceCanReach(destination))
                    throw new NotSupportedException("Backups to a network share without a saved user name and password run from the tray app, not the background service.");
                continue;
            }
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
