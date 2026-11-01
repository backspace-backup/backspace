using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.AccessControl;
using System.ServiceProcess;
using Backspace.Models;
using Microsoft.Win32.TaskScheduler;

namespace Backspace.Services.Platform.Windows;

/// <summary>With administrator access on, the background service keeps the
/// schedule itself (U043, 31 October 2026): it starts with Windows and runs
/// due backups before anyone signs in. Windows keeps only this copy's
/// "Apply saved settings" task, which has no trigger. Folders made by earlier
/// versions also held one SYSTEM task per backup and a sign-in task for alerts;
/// the next settings update removes them.</summary>
internal static class ServiceScheduleRegistration
{
    internal const string LegacyDesktopTask = "Desktop notifications";
    internal static string Folder(ServiceWorkerBinding binding) => "\\" + binding.ServiceName;

    /// <summary>True while this copy has background-service setup files,
    /// unless a completed removal returned the schedules to the tray app.</summary>
    internal static bool OwnsSchedules(string root)
        => File.Exists(ServicePortableLayout.Record(root, ServiceWorkerBinding.FileName))
            && ServiceRetirement.Read(root) is not { Stage: "complete" };

    /// <summary>The service starts with Windows. After an update, a settings
    /// change or a failure stopped it, the owner's app starts it again; the
    /// owner may start it but not change it.</summary>
    internal static void StartIfStopped(string root)
    {
        if (ServiceRetirement.Read(root) is not null || ServiceProgramUpdate.IsPending(root)) return;
        var binding = ServiceWorkerBinding.LoadForOwner(root);
        if (!binding.CanRun) return;
        using var service = new ServiceController(binding.ServiceName);
        if (service.StartType == ServiceStartMode.Automatic && service.Status == ServiceControllerStatus.Stopped)
        {
            ServiceRegistration.StartUnlessStarted(() => { service.Refresh(); return service.Status; }, service.Start);
            AppLogger.For(nameof(ServiceScheduleRegistration)).Information("Started the background service, which keeps the schedule");
        }
    }

    /// <summary>A saved backup runs on schedule once the service has its settings.</summary>
    internal static void RequireApplied(string root)
    {
        var binding = ServiceWorkerBinding.LoadForOwner(root);
        if (!binding.Activated)
            throw new NotSupportedException("Finish setting up scheduled backups in Settings before this schedule can run.");
        var approval = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, binding.OwnerSid).Snapshot();
        ServiceConfigurationComparison.RequireCurrent(root, approval.Configuration);
    }

    internal static ScheduleCheck Inspect(string root, string executable, string backupId)
    {
        if (ServiceRetirement.Read(root) is not null)
            return new(ScheduleReadiness.Attention, "Running all the time is being switched off. Check Settings → Administrator access.");
        var binding = ServiceWorkerBinding.LoadForOwner(root);
        if (!binding.Activated) return new(ScheduleReadiness.Attention, "Finish setup in Settings → Administrator access.");
        var approval = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, binding.OwnerSid).Snapshot();
        if (File.Exists(ServicePortableLayout.Record(root, ServiceConfigurationUpdate.FileName))
            || !ServiceConfigurationComparison.Equivalent(approval.Configuration, ServiceConfigurationComparison.ReadDesktop(root)))
            return new(ScheduleReadiness.Attention, "Your latest changes aren't applied yet. Check Settings → Administrator access.");
        if (approval.Configuration.Backups.All(b => b.Id != backupId))
            return new(ScheduleReadiness.Attention, "This backup isn't part of the background service's settings yet. Check Settings → Administrator access.");
        using (var service = new ServiceController(binding.ServiceName))
        {
            if (service.StartType != ServiceStartMode.Automatic)
                return new(ScheduleReadiness.Attention, "Windows is no longer set to start the background service. Finish setup in Settings.");
            if (service.Status != ServiceControllerStatus.Running)
                return new(ScheduleReadiness.Attention, "The background service isn't running. Open Settings → Administrator access.");
        }
        using var scheduler = new TaskService();
        using var task = scheduler.GetTask(Folder(binding) + "\\" + ServiceSettingsTask.Name);
        if (task is null || !task.Enabled)
            return new(ScheduleReadiness.Attention, "Applying your changes needs setup. Open Settings → Administrator access.");
        using var definition = task.Definition;
        ValidateTask(task, executable, definition, binding);
        return new(ScheduleReadiness.Ready, "The background service runs this backup on schedule.");
    }

    internal static IEnumerable<(Backup Backup, ScheduledTaskKind Kind, BackupSchedule Schedule)> Plans(AppConfig config)
        => SchedulePlan.From(config).Select(p => (p.Backup, p.Kind, p.Schedule));

    // Preparation replaces only this installation's authenticated task folder.
    // The settings task stays disabled until the credential activation commit.
    internal static void Prepare(ServiceWorkerBinding binding, string executable, ServiceRunAuthorization.Approval approval)
    {
        ServiceRegistration.RequireAdministrator();
        using var scheduler = new TaskService();
        // The service may read the folder and start its settings task (U061).
        var sddl = $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;{binding.OwnerSid})(A;;FR;;;{binding.ServiceSid})";
        using var folder = scheduler.GetFolder(Folder(binding))
            ?? scheduler.RootFolder.CreateFolder(binding.ServiceName, sddl, exceptionOnExists: true);
        ValidateFolder(folder);
        // Validate everything before changing anything.
        foreach (var task in folder.Tasks)
            using (task) { using var definition = task.Definition; ValidateTask(task, executable, definition, binding); }
        // The settings task usually runs this very update. Re-registering or
        // disabling it here would leave it disabled if the update then failed,
        // and no later change could reach the service (VM, 31 October 2026).
        // An activated installation keeps its valid task as it is.
        using (var existing = scheduler.GetTask(Folder(binding) + "\\" + ServiceSettingsTask.Name))
        {
            if (existing is null || !binding.Activated)
            {
                using var definition = ServiceSettingsTask.Build(scheduler, executable);
                definition.Settings.Enabled = false;
                using var task = folder.RegisterTaskDefinition(ServiceSettingsTask.Name, definition,
                    TaskCreation.CreateOrUpdate | TaskCreation.DontAddPrincipalAce, "SYSTEM", null,
                    TaskLogonType.ServiceAccount, ServiceSettingsTask.Sddl(binding));
                ValidateTask(task, executable, definition, binding);
            }
        }
        RemoveLegacy(folder);
    }

    internal static void Enable(ServiceWorkerBinding binding, string executable, ServiceRunAuthorization.Approval approval)
    {
        ServiceRegistration.RequireAdministrator();
        using var scheduler = new TaskService();
        using var folder = scheduler.GetFolder(Folder(binding)) ?? throw new IOException("The settings task is missing.");
        ValidateFolder(folder);
        foreach (var task in folder.Tasks)
            using (task) { using var definition = task.Definition; ValidateTask(task, executable, definition, binding); }
        RemoveLegacy(folder);
        using var settings = folder.Tasks[ServiceSettingsTask.Name];
        using var settingsDefinition = settings.Definition;
        ValidateTask(settings, executable, settingsDefinition, binding);
        settings.Enabled = true;
    }

    /// <summary>True while an earlier version's per-backup or sign-in task remains.</summary>
    internal static bool HasLegacyTasks(ServiceWorkerBinding binding)
    {
        using var scheduler = new TaskService();
        using var folder = scheduler.GetFolder(Folder(binding));
        if (folder is null) return false;
        foreach (var task in folder.Tasks)
            using (task) if (task.Name != ServiceSettingsTask.Name) return true;
        return false;
    }

    /// <summary>Run by the settings task itself, which must stay registered.</summary>
    internal static void RemoveLegacyTasks(ServiceWorkerBinding binding, string executable)
    {
        using var scheduler = new TaskService();
        using var folder = scheduler.GetFolder(Folder(binding));
        if (folder is null) return;
        ValidateFolder(folder);
        foreach (var task in folder.Tasks)
            using (task) { using var definition = task.Definition; ValidateTask(task, executable, definition, binding); }
        RemoveLegacy(folder);
    }

    private static void RemoveLegacy(TaskFolder folder)
    {
        foreach (var task in folder.Tasks)
            using (task)
                if (task.Name != ServiceSettingsTask.Name)
                {
                    task.Enabled = false;
                    folder.DeleteTask(task.Name, exceptionOnNotExists: false);
                }
    }

    private static void ValidateFolder(TaskFolder folder)
        => ServiceInstallationSecurity.ValidateDescriptor(new RawSecurityDescriptor(
            folder.GetSecurityDescriptorSddlForm(SecurityInfos.Owner | SecurityInfos.DiscretionaryAcl)),
            content: true, ServiceInstallationSecurity.IsTrustedInstallerIdentity);

    internal static void DisableOrRemove(ServiceWorkerBinding binding, string executable, bool remove)
    {
        ServiceRegistration.RequireAdministrator();
        using var scheduler = new TaskService();
        using var folder = scheduler.GetFolder(Folder(binding));
        if (folder is null) return;
        ValidateFolder(folder);
        // Validate the complete folder before changing even the first task.
        foreach (var task in folder.Tasks)
            using (task) { using var definition = task.Definition; ValidateTask(task, executable, definition, binding); }
        foreach (var task in folder.Tasks)
            using (task)
            {
                task.Enabled = false;
                if (remove) folder.DeleteTask(task.Name);
            }
        if (remove) scheduler.RootFolder.DeleteFolder(binding.ServiceName, exceptionOnNotExists: false);
    }

    /// <summary>Only this copy's settings task, or an earlier version's
    /// backup or sign-in task for this copy, may be changed or removed.</summary>
    internal static void ValidateTask(Microsoft.Win32.TaskScheduler.Task task, string executable, TaskDefinition definition, ServiceWorkerBinding binding)
    {
        ServiceInstallationSecurity.ValidateDescriptor(new RawSecurityDescriptor(
            task.GetSecurityDescriptorSddlForm(SecurityInfos.Owner | SecurityInfos.DiscretionaryAcl)),
            content: true, ServiceInstallationSecurity.IsTrustedInstallerIdentity);
        if (task.Name == ServiceSettingsTask.Name) { ServiceSettingsTask.Validate(definition, executable); return; }
        var action = definition.Actions.Count == 1 ? definition.Actions[0] as ExecAction : null;
        if (action is null || !string.Equals(action.Path, executable, StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("An unexpected task exists in this service's folder. It was not replaced.");
        if (task.Name == LegacyDesktopTask)
        {
            if (definition.Principal.LogonType != TaskLogonType.InteractiveToken || action.Arguments != "--service-desktop"
                || !LegacyUserTasks.SameWindowsUser(definition.Principal.UserId, binding.OwnerSid))
                throw new SecurityException("The earlier sign-in task differs from this portable installation.");
            return;
        }
        if (definition.Principal.LogonType != TaskLogonType.ServiceAccount
            || !LegacyUserTasks.SameWindowsUser(definition.Principal.UserId, "S-1-5-18")
            || !action.Arguments.StartsWith("--service-scheduled ", StringComparison.Ordinal))
            throw new SecurityException("An unexpected task exists in this service's folder. It was not replaced.");
    }
}
