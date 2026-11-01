using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Backspace.Services.Platform.Windows;

/// <summary>Retire only old tasks whose account, name and complete action
/// identify this portable copy. Prefix matching alone never authorizes deletion.
/// No old S4U definition is executed or restored by this migration.</summary>
internal static class ServiceLegacyTasks
{
    private sealed record Archive(int Version, string ServiceName, string TaskPath, string Definition);
    private static readonly string[] Prefixes = { "Backspace — ", "EDB — ", "Duplimate — " };

    internal static bool Matches(string name, TaskDefinition definition, string executable, string ownerSid, IEnumerable<string> stubs)
    {
        var prefix = Prefixes.FirstOrDefault(p => name.StartsWith(p, StringComparison.Ordinal));
        if (prefix is null || definition.Actions.Count != 1 || definition.Actions[0] is not ExecAction action
            || definition.Principal.LogonType is not (TaskLogonType.S4U or TaskLogonType.InteractiveToken)
            || !LegacyUserTasks.SameWindowsUser(definition.Principal.UserId, ownerSid)) return false;
        var tail = name[prefix.Length..];
        if (string.IsNullOrWhiteSpace(tail)) return false;
        var root = Path.GetDirectoryName(executable)!;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var kind in new[] { ScheduledTaskKind.Backup, ScheduledTaskKind.Check, ScheduledTaskKind.Prune })
        {
            var suffix = kind == ScheduledTaskKind.Backup ? "" : " (" + kind + ")";
            if (!tail.EndsWith(suffix, StringComparison.Ordinal) || tail.Length <= suffix.Length) continue;
            var backup = suffix.Length == 0 ? tail : tail[..^suffix.Length];
            var flag = kind == ScheduledTaskKind.Check ? "--check" : kind == ScheduledTaskKind.Prune ? "--prune" : "--run";
            if (Equal(action.Path, executable) && (string.IsNullOrEmpty(action.WorkingDirectory) || Equal(action.WorkingDirectory, root))
                && action.Arguments == flag + " " + LegacyUserTasks.QuoteWindowsArgument(backup)) return true;
            foreach (var stub in stubs)
            {
                using var expected = LegacyUserTasks.BuildExecAction(executable, stub, backup, kind);
                if (Equal(action.Path, expected.Path) && Equal(action.WorkingDirectory, expected.WorkingDirectory) && action.Arguments == expected.Arguments) return true;
                // Exact bytes from the original cmd-based implementation. This
                // is comparison only; do not execute or interpret the script.
                static string Quote(string value) => "\"\"" + value.Replace("\"", "\"\"\"\"") + "\"\"";
                var oldArguments = "/D /S /C \"if exist " + Quote(executable) + " ( " + Quote(executable) + " " + flag + " " + Quote(backup) + " ) else ( " + Quote(stub) + " )\"";
                if ((Equal(action.Path, "cmd.exe") || Equal(action.Path, Path.Combine(system, "cmd.exe")))
                    && Equal(action.WorkingDirectory, root) && action.Arguments == oldArguments) return true;
            }
        }
        return false;
    }

    internal static void Disable(string root, ServiceWorkerBinding binding) => Visit(root, binding, remove: false);
    internal static void Remove(string root, ServiceWorkerBinding binding) => Visit(root, binding, remove: true);

    internal static bool HasRemaining(string root, ServiceWorkerBinding binding)
    {
        using var scheduler = new TaskService();
        using var folder = scheduler.GetFolder(LegacyUserTasks.FolderPath);
        if (folder is null) return false;
        var stubs = KnownStubs(root, binding.OwnerSid).ToArray();
        foreach (var task in folder.Tasks)
            using (task)
            using (var definition = task.Definition)
                if (Matches(task.Name, definition, Path.Combine(root, "Backspace.exe"), binding.OwnerSid, stubs)) return true;
        return false;
    }

    private static void Visit(string root, ServiceWorkerBinding binding, bool remove)
    {
        ServiceRegistration.RequireAdministrator();
        var executable = Path.Combine(root, "Backspace.exe");
        ServiceRegistration.VerifyInstalled(binding, executable);
        var stubs = KnownStubs(root, binding.OwnerSid).ToArray();
        using var scheduler = new TaskService();
        using var folder = scheduler.GetFolder(LegacyUserTasks.FolderPath);
        if (folder is null) return;
        var running = false;
        foreach (var task in folder.Tasks)
        {
            using (task)
            {
                var originalXml = task.Xml;
                using var definition = scheduler.NewTask();
                definition.XmlText = originalXml;
                if (!Matches(task.Name, definition, executable, binding.OwnerSid, stubs)) continue;
                var record = "legacy-task-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(task.Path))) + ".json";
                var path = ServicePortableLayout.Record(root, record);
                if (!File.Exists(path)) ServiceSetup.WriteRecord(root, record,
                    JsonSerializer.SerializeToUtf8Bytes(new Archive(1, binding.ServiceName, task.Path, originalXml), ServiceRunAuthorization.Json));
                else
                {
                    ServiceInstallationSecurity.ValidateProtectedRecord(path, root);
                    using var saved = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (saved.Length is <= 0 or > 262144) throw new IOException("The earlier schedule's saved definition could not be checked.");
                    var prior = JsonSerializer.Deserialize<Archive>(saved, ServiceRunAuthorization.Json);
                    if (prior?.Version != 1 || prior.ServiceName != binding.ServiceName || prior.TaskPath != task.Path)
                        throw new IOException("The earlier schedule's saved definition belongs to another installation.");
                }
                using var current = scheduler.GetTask(task.Path);
                if (current is null) continue;
                if (current.Xml != originalXml)
                    throw new IOException("An earlier Windows task changed during setup. No further tasks were removed; retry after reviewing the change.");
                current.Enabled = false;
                if (current.State == TaskState.Running) { running = true; continue; }
                if (remove) folder.DeleteTask(current.Name, exceptionOnNotExists: false);
            }
        }
        if (running) throw new IOException("An earlier Backspace task is still running. Its next run is disabled. Let the current operation finish, then continue automatic-backup setup.");
    }

    private static IEnumerable<string> KnownStubs(string root, string ownerSid)
    {
        yield return Path.Combine(ServicePortableLayout.Config(root), "Backspace-stub.exe");
        using var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + ownerSid);
        if (profile?.GetValue("ProfileImagePath") is string directory && Path.IsPathFullyQualified(directory))
            yield return Path.Combine(directory, "AppData", "Local", "Programs", "Backspace", "Backspace-stub.exe");
    }
    private static bool Equal(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
