using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Applies the owner's saved backup settings to the background service
/// without a Windows prompt (U043, 31 October 2026). It is registered once,
/// with the administrator approval that turns access on; afterwards the owner
/// may start it but not change it. It runs as SYSTEM with one fixed argument
/// and applies the owner's own saved settings through the same validated
/// update as the approval it replaces (paths, schedules, locks, repository
/// access). The owner could already change those settings and decides where
/// Backspace lives (accepted risk, U024), so asking Windows again for every
/// edit protected little and paused every schedule until someone noticed.
/// </summary>
internal static class ServiceSettingsTask
{
    internal const string Name = "Apply saved settings";
    internal const string Argument = "--service-apply-settings";
    /// <summary>A backup or restore holds the operations lock; try again after it.</summary>
    internal const int BusyExitCode = 5;

    internal static string Sddl(ServiceWorkerBinding binding)
        => $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;{binding.OwnerSid})(A;;FRFX;;;{binding.ServiceSid})";

    internal static TaskDefinition Build(TaskService scheduler, string executable)
    {
        var task = scheduler.NewTask();
        task.RegistrationInfo.Description = "Apply this portable Backspace copy's saved backup settings to its background service. Started by Backspace when you save a change.";
        task.Principal.UserId = "S-1-5-18";
        task.Principal.LogonType = TaskLogonType.ServiceAccount;
        task.Principal.RunLevel = TaskRunLevel.Highest;
        task.Actions.Add(new ExecAction(executable, Argument, Path.GetDirectoryName(executable)));
        task.Settings.MultipleInstances = TaskInstancesPolicy.Queue;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = TimeSpan.FromMinutes(30);
        task.Settings.AllowDemandStart = true;
        task.Settings.Hidden = true;
        return task;
    }

    internal static void Validate(TaskDefinition task, string executable)
    {
        if (task.Principal.LogonType != TaskLogonType.ServiceAccount
            || !LegacyUserTasks.SameWindowsUser(task.Principal.UserId, "S-1-5-18")
            || task.Actions.Count != 1 || task.Actions[0] is not ExecAction action
            || !string.Equals(action.Path, executable, StringComparison.OrdinalIgnoreCase) || action.Arguments != Argument
            || task.Triggers.Count != 0 || !task.Settings.AllowDemandStart)
            throw new SecurityException("The settings task differs from this portable installation.");
    }

    /// <summary>The task's action, run by Windows as SYSTEM.</summary>
    internal static int ApplyAsSystem(string installation)
    {
        using (var identity = WindowsIdentity.GetCurrent())
            if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                throw new SecurityException("Only the installed settings task may apply settings this way.");
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        try
        {
            // Finish an interrupted update first, then apply what is saved now.
            // One interrupted after its policy was saved counts too: otherwise
            // every later update is refused (release VM check, 31 October 2026).
            var binding = ServiceWorkerBinding.Load(root);
            if (ServiceConfigurationUpdate.IsUnfinished(root, binding)) ServiceConfigurationUpdate.Apply(root, null);
            var current = ServiceConfigurationComparison.ReadDesktop(root);
            var approval = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, binding.OwnerSid).Snapshot();
            if (!ServiceConfigurationComparison.Equivalent(approval.Configuration, current))
                ServiceConfigurationUpdate.Apply(root, ServiceRunAuthorization.Digest(current));
            // Versions before U043 left one Windows task per backup and a
            // sign-in task. The service keeps the schedule now (U043).
            else if (ServiceScheduleRegistration.HasLegacyTasks(binding))
                ServiceScheduleRegistration.RemoveLegacyTasks(binding, Path.Combine(root, "Backspace.exe"));
            // The service starts this task when a plugged-in drive's backup
            // folder is not writable for it yet (U061).
            var applied = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, binding.OwnerSid).Snapshot();
            ServiceRepositoryPermissions.GrantConnectedExternal(root, binding, applied);
            ForgetRemovedBackups(Path.Combine(binding.DataDirectory, "logs", "engine"),
                Path.Combine(ServicePortableLayout.Config(root), "pending-removals.json"), current);
            return 0;
        }
        catch (ServiceBusyException) { return BusyExitCode; }
    }

    /// <summary>Removing a backup deletes its run history (U029). Runs made by
    /// the service keep theirs in its protected folder, which only SYSTEM or
    /// the service can change, so it is cleared here: for backups no longer
    /// saved and not still waiting for an unplugged drive's copies to go.</summary>
    internal static void ForgetRemovedBackups(string engine, string pending, Backspace.Models.AppConfig current)
    {
        if (!Directory.Exists(engine)) return;
        var keep = new System.Collections.Generic.HashSet<string>(current.Backups.Select(b => b.Id), StringComparer.Ordinal);
        if (File.Exists(pending))
        {
            // Unreadable: keep everything rather than guess.
            try { foreach (var id in Ids(File.ReadAllText(pending), "backup")) keep.Add(id); }
            catch (Exception) { return; }
        }
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(engine))
        {
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var name in new[] { "history.json", "timeline.json" })
            {
                var file = Path.Combine(directory, name);
                try { if (File.Exists(file)) foreach (var id in Ids(File.ReadAllText(file), null)) seen.Add(id); }
                catch (Exception) { }
            }
        }
        var store = new LogStore(engine);
        foreach (var id in seen.Where(id => !keep.Contains(id))) store.ForgetBackup(id);
    }

    /// <summary>Backup IDs in a run-history array, or in the named child
    /// object of each entry (pending removals).</summary>
    private static System.Collections.Generic.IEnumerable<string> Ids(string json, string? child)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var ids = new System.Collections.Generic.List<string>();
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return ids;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var holder = entry;
            if (child is not null && !TryGet(entry, child, out holder)) continue;
            if (TryGet(holder, child is null ? "backupId" : "id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String) ids.Add(id.GetString()!);
        }
        return ids;
    }

    private static bool TryGet(System.Text.Json.JsonElement element, string name, out System.Text.Json.JsonElement value)
    {
        value = default;
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        return false;
    }

    /// <summary>Desktop side: start the task and wait for it. Null when this
    /// installation has no usable settings task yet (set up by an earlier
    /// version); one approval through the usual prompt registers it.</summary>
    internal static async System.Threading.Tasks.Task<int?> RunAsync(ServiceWorkerBinding binding, string executable, CancellationToken cancellation)
    {
        var path = ServiceScheduleRegistration.Folder(binding) + "\\" + Name;
        DateTime before;
        using (var scheduler = new TaskService())
        {
            using var task = scheduler.GetTask(path);
            if (task is null || !task.Enabled) return null;
            using var definition = task.Definition;
            ServiceScheduleRegistration.ValidateTask(task, executable, definition, binding);
            before = task.LastRunTime;
            task.Run();
        }
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, cancellation).ConfigureAwait(false);
            using var scheduler = new TaskService();
            using var task = scheduler.GetTask(path);
            if (task is null) return null;
            if (task.State != TaskState.Running && task.State != TaskState.Queued && task.LastRunTime > before)
                return task.LastTaskResult;
        }
        throw new TimeoutException("Applying your settings is taking unusually long. It continues in the background.");
    }
}

/// <summary>A backup, restore or cleanup is running; settings are applied after it.</summary>
internal sealed class ServiceBusyException(string message) : IOException(message);
