using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Existing public CLI commands enter through the owner's authenticated
/// service connection. They do not read the private vault or start a second
/// engine in the desktop account.</summary>
internal static class ServiceCommandLineRun
{
    internal static ServiceWorkerBinding? FindBinding(string root, string configuration)
    {
        if (!ServiceOwnerSecrets.Applies(root, configuration)) return null;
        if (ServiceRetirement.Read(root) is { Stage: "complete" }) return null;
        var path = ServicePortableLayout.Record(root, ServiceWorkerBinding.FileName);
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        ServiceRetirement.RequireNotRetiring(root);
        var binding = ServiceWorkerBinding.LoadForOwner(root);
        if (!binding.Activated) throw new IOException("Finish setup in Settings → Administrator access before using command-line backups.");
        return binding;
    }

    /// <summary>True when this copy's background service is set up and active.</summary>
    internal static bool IsActive(string root)
    {
        try { return FindBinding(root, AppPaths.ConfigRoot) is not null; }
        catch (Exception) { return false; }
    }

    /// <summary>Run now from the desktop through the service, so files in use
    /// are included without a Windows prompt (U026, 29 October 2026). False,
    /// with nothing started, when the service is not active, the saved
    /// settings still need approval or this backup is not approved; the
    /// caller then runs it itself. Once the start request is sent the result
    /// belongs to the service: a lost reply must not cause a second run, so
    /// failures after that point are thrown, never reported as false. The
    /// card follows progress and the result through the service's activity,
    /// except when the service started nothing (a paused schedule or
    /// session): <paramref name="notStarted"/> then says why.</summary>
    internal static async Task<bool> TryRunFromDesktopAsync(string root, Backup backup, CancellationToken cancellation, Action<string>? notStarted = null)
    {
        ServiceWorkerBinding binding;
        string revision;
        try
        {
            if (FindBinding(root, AppPaths.ConfigRoot) is not { } found) return false;
            var approval = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, found.OwnerSid).Snapshot();
            if (File.Exists(ServicePortableLayout.Record(root, ServiceConfigurationUpdate.FileName))
                || !ServiceConfigurationComparison.Equivalent(approval.Configuration, ServiceConfigurationComparison.ReadDesktop(root))
                || approval.Configuration.Backups.SingleOrDefault(b => b.Id == backup.Id) is not { Enabled: true } approved
                || !SchedulePlan.ServiceCanRun(approved, approval.Configuration.Destinations))
                return false;
            (binding, revision) = (found, approval.Revision);
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLogger.For<BackupWindowsService>().Information(ex, "Run now stays in the desktop for {Backup}", backup.Name);
            return false;
        }
        await ServiceScheduledRun.RunApprovedAsync(root, binding, revision, backup.Id, "backup", cancellation,
            status => { if (status.Contains("Nothing was started", StringComparison.Ordinal)) notStarted?.Invoke(status); }).ConfigureAwait(false);
        return true;
    }

    internal static IReadOnlyList<Backup> SelectBackups(AppConfig config, string? name)
    {
        if (name is null) return config.Backups.Where(b => b.Enabled).ToArray();
        var matches = config.Backups.Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new IOException("More than one backup has this name. Give them distinct names before running this command.");
        if (matches.Length == 1 && !matches[0].Enabled)
            throw new IOException("This backup is disabled. Enable it and apply the change to automatic backups first.");
        return matches;
    }

    internal static async Task<int> RunAsync(string root, ServiceWorkerBinding binding, string? name,
        string operation, TextWriter output, CancellationToken cancellation)
    {
        if (operation is not ("backup" or "check" or "prune") || name is null && operation != "backup")
            throw new ArgumentException("Unsupported command-line operation.");
        var approval = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, binding.OwnerSid).Snapshot();
        ServiceConfigurationComparison.RequireCurrent(root, approval.Configuration);
        var backups = SelectBackups(approval.Configuration, name);
        if (backups.Count == 0)
        {
            await output.WriteLineAsync(name is null ? "No enabled backups." : $"No backup named '{name}' found.").ConfigureAwait(false);
            return 2;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var result = 0;
            foreach (var backup in backups)
            {
                stop.Token.ThrowIfCancellationRequested();
                await output.WriteLineAsync($"=== {backup.Name} :: {operation} ===").ConfigureAwait(false);
                var code = await ServiceScheduledRun.RunApprovedAsync(root, binding, approval.Revision, backup.Id,
                    operation, stop.Token, output.WriteLine).ConfigureAwait(false);
                if (code != 0) result = code;
            }
            return result;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { await output.WriteLineAsync("Cancellation requested. Check Activity & logs for the final outcome.").ConfigureAwait(false); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
