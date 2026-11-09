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
        if (!ServicePortableLayout.IsPortableConfig(root, configuration)) return null;
        return SystemService.ForCopy(root) is { Activated: true } system ? system.Binding : null;
    }

    /// <summary>True when this copy's background service is set up and active.</summary>
    internal static bool IsActive(string root)
    {
        try { return FindBinding(root, AppPaths.ConfigRoot) is not null; }
        catch (Exception) { return false; }
    }

    /// <summary>Run now from the desktop through the service, so files in use
    /// are included without a Windows prompt (U026, 29 October 2026). This
    /// includes backups with automatic backups off (U171): the owner asked
    /// for this run. False, with nothing started, only when the service is
    /// not set up; the caller then runs it itself. With the service set up,
    /// every backup goes through it: one it can't run, or settings it can't
    /// read, throw with the reason, so nothing falls back to the window
    /// without a snapshot. Once the start request is sent the result
    /// belongs to the service: a lost reply must not cause a second run, so
    /// failures after that point are thrown, never reported as false. The
    /// card follows progress and the result through the service's activity,
    /// except when the service started nothing (a paused schedule or
    /// session): <paramref name="notStarted"/> then says why.</summary>
    internal static async Task<bool> TryRunFromDesktopAsync(string root, Backup backup, CancellationToken cancellation, Action<string>? notStarted = null,
        Action<string>? queued = null)
    {
        ServiceWorkerBinding binding;
        try
        {
            if (FindBinding(root, AppPaths.ConfigRoot) is not { } found) return false;
            binding = found;
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLogger.For<SystemBackupService>().Information(ex, "Run now stays in the desktop for {Backup}: the background service could not be found", backup.Name);
            return false;
        }
        // The saved settings are what the service runs (U100). A read failure
        // is thrown, never a reason to run in the window (U171).
        if (Refusal(ServiceConfigurationComparison.ReadDesktop(root), backup.Id) is { } refusal)
            throw new InvalidOperationException(refusal);
        var revision = Guid.NewGuid().ToString("N");
        await ServiceScheduledRun.RunApprovedAsync(root, binding, revision, backup.Id, "backup", cancellation,
            status => { if (status.Contains("Nothing was started", StringComparison.Ordinal)) notStarted?.Invoke(status); }, queued).ConfigureAwait(false);
        return true;
    }

    /// <summary>Whether Run now or Save and run hands this saved backup to the
    /// active service: whenever the service can run it, automatic backups
    /// on or off (U171).</summary>
    internal static bool RunsInService(AppConfig saved, string backupId) => Refusal(saved, backupId) is null;

    /// <summary>Why the active service can't run this backup, for the person;
    /// null when it can. The run then fails with this reason: it never runs
    /// in the window instead (U171).</summary>
    internal static string? Refusal(AppConfig saved, string backupId)
        => saved.Backups.SingleOrDefault(b => b.Id == backupId) is { } current
            ? SystemServiceRuns.Refusal(current, saved)
            : "This backup isn't saved yet. Save it, then run it.";

    /// <summary>The backups a command runs. Without a name, those with
    /// automatic backups on; a backup named explicitly runs even with
    /// automatic backups off, as Run now does in the window (U171).</summary>
    internal static IReadOnlyList<Backup> SelectBackups(AppConfig config, string? name)
    {
        if (name is null) return config.Backups.Where(b => b.Enabled).ToArray();
        var matches = config.Backups.Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new IOException("More than one backup has this name. Give them distinct names before running this command.");
        return matches;
    }

    internal static async Task<int> RunAsync(string root, ServiceWorkerBinding binding, string? name,
        string operation, TextWriter output, CancellationToken cancellation)
    {
        if (operation is not ("backup" or "check" or "prune") || name is null && operation != "backup")
            throw new ArgumentException("Unsupported command-line operation.");
        var configuration = ServiceConfigurationComparison.ReadDesktop(root);
        var revision = Guid.NewGuid().ToString("N");
        var backups = SelectBackups(configuration, name);
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
                var code = await ServiceScheduledRun.RunApprovedAsync(root, binding, revision, backup.Id,
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
