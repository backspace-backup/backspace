using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>Replace the same portable service bundle under administrator
/// approval. The unprivileged caller supplies only a release tag and staging
/// ID; release authority, paths, hashes and recovery state are verified here.</summary>
internal static class ServiceProgramUpdate
{
    internal const string FileName = "program-update.json";
    internal const string LastFileName = "last-program-update.json";
    internal sealed record Pending(int Version, string ServiceName, WindowsUpdatePackage.Plan Plan,
        ServiceSetupRecovery.Journal OriginalPermissions, bool Ready = false);

    internal static void ValidateRequest(string tag, string stagingId)
    {
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || tag.Length > 40
            || !Guid.TryParseExact(stagingId, "N", out _))
            throw new SecurityException("The requested update identity is invalid.");
    }

    internal static async Task<WindowsUpdatePackage.Release> ReadReleaseAsync(HttpClient http, string tag, CancellationToken ct)
    {
        ValidateRequest(tag, Guid.Empty.ToString("N"));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/backspace-backup/backspace/releases/tags/" + tag);
        request.Headers.UserAgent.ParseAdd("Backspace-Updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("The release response is too large.");
            bytes.Write(buffer, 0, count);
        }
        var release = WindowsUpdatePackage.ParseRelease(System.Text.Encoding.UTF8.GetString(bytes.ToArray()), WindowsUpdatePackage.CurrentVersion)
            ?? throw new IOException("This is no longer an available newer stable release. Check for updates again.");
        if (release.Tag != tag) throw new SecurityException("GitHub returned a different release. The update was not installed.");
        return release;
    }

    internal static bool IsPending(string root) => File.Exists(ServicePortableLayout.Record(root, FileName));
    internal static void RequireNoPending(string root)
    {
        if (IsPending(root)) throw new IOException("Finish recovering the application update before changing background backups.");
    }

    internal static async Task ApplyAsync(string installation, string tag, string stagingId, HttpClient http, CancellationToken ct)
    {
        ServiceRegistration.RequireAdministrator(); ValidateRequest(tag, stagingId);
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        ServiceRetirement.RequireNotRetiring(root);
        var binding = ServiceWorkerBinding.Load(root);
        if (!binding.Activated) throw new IOException("Finish background backup setup before installing an update.");
        ServiceRegistration.VerifyInstalled(binding, Path.Combine(root, "Backspace.exe"));
        ServiceInstallationSecurity.ValidateCodeTree(root, binding.DataDirectory, binding.ServiceSid);
        // Re-fetch the selected release over the fixed official endpoint after
        // elevation. Never trust hashes, URLs or executable paths from the GUI.
        var release = await ReadReleaseAsync(http, tag, ct).ConfigureAwait(false);
        using var setup = StateFileLock.Acquire(ServicePortableLayout.Record(root, "activation"));
        ServiceRetirement.RequireNotRetiring(root);
        if (ServiceWorkerBinding.Load(root) != binding) throw new IOException("Background backup setup changed during the update check. Try again.");
        ServiceRegistration.VerifyInstalled(binding, Path.Combine(root, "Backspace.exe"));
        RequireNoPending(root);
        if (File.Exists(ServicePortableLayout.Record(root, ServiceConfigurationUpdate.FileName)))
            throw new IOException("Finish applying the pending backup settings before updating the application.");
        using var operations = ServiceCoordinationFiles.TryOpen(new(root, binding), ServiceCoordinationFiles.OperationFile, true)
            ?? throw new IOException("A backup, restore or cleanup is running. The update was not installed.");
        using var runtime = WindowsUpdateRuntime.TryExclusive(root)
            ?? throw new IOException("Another Backspace window or command is still using this copy. Close it before updating.");
        var work = CreateWork(root);
        var replacementStarted = false;
        try
        {
        var archive = Path.Combine(work, "download.zip");
        // The staging tree belongs to the desktop owner. Hold every component
        // and the inspected regular file against rename/write before copying.
        var stagingParent = Path.Combine(ServicePortableLayout.Config(root), ".backspace-updates");
        using (var parent = new ServiceObjectPermissions(stagingParent))
        using (var directory = new ServiceObjectPermissions(Path.Combine(stagingParent, stagingId)))
        using (var file = new ServiceObjectPermissions(Path.Combine(stagingParent, stagingId, "download.zip"), directory: false))
        {
            var source = Path.Combine(stagingParent, stagingId, "download.zip");
            if (new FileInfo(source).Length != release.Size) throw new InvalidDataException("The staged download size changed.");
            File.Copy(source, archive, overwrite: false);
        }
        if (!WindowsUpdatePackage.Hash(archive).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The staged download no longer matches the official release checksum.");
        var plan = WindowsUpdatePackage.Extract(archive, root, work, "Backspace.exe", release.Version.ToString(), ct, serviceUpdate: true);
        WindowsUpdatePackage.ValidateApplicationVersion(plan, release.Version);
        replacementStarted = true;
        InstallPrepared(root, binding, plan);
        }
        finally
        {
            if (!replacementStarted) WindowsUpdatePackage.SafeRemoveWork(root, work, serviceUpdate: true);
        }
    }

    internal static void InstallPrepared(string root, ServiceWorkerBinding binding, WindowsUpdatePackage.Plan plan, Action<string>? checkpoint = null)
    {
        ServiceRegistration.RequireAdministrator();
        RequireNoPending(root);
        ValidatePlan(plan, root);
        if (plan.State != "Prepared") throw new IOException("This update has already started. Use its saved recovery record.");
        var journal = ReadTrusted<ServiceSetupRecovery.Journal>(root, ServiceSetupRecovery.FileName);
        ServiceSetupRecovery.Validate(journal, root);
        if (journal.Binding.ServiceName != binding.ServiceName || journal.Binding.OwnerSid != binding.OwnerSid)
            throw new SecurityException("The original permissions belong to another service.");
        ValidateOriginalIdentities(root, journal);
        WriteRecoveryLauncher(plan);
        var pending = new Pending(1, binding.ServiceName, plan, journal);
        Write(root, pending); // Block admission before disabling anything.
        checkpoint?.Invoke("intent-saved");
        try
        {
            ServiceScheduleRegistration.DisableOrRemove(binding, Path.Combine(root, "Backspace.exe"), remove: false);
            ServiceRegistration.QuiesceForSetupRecovery(binding, Path.Combine(root, "Backspace.exe"));
            checkpoint?.Invoke("service-stopped");
            WindowsUpdatePackage.Install(plan, _ => { }, n => checkpoint?.Invoke("file-move-" + n));
            checkpoint?.Invoke("files-installed");
            var identities = new Dictionary<string, string>(journal.Identities, StringComparer.OrdinalIgnoreCase);
            foreach (var name in ServiceSetup.ProgramFiles)
            {
                using var held = new ServiceObjectPermissions(Path.Combine(root, name), directory: false);
                identities[name] = held.Identity;
            }
            ServiceInstallationSecurity.ValidateCodeTree(root, binding.DataDirectory, binding.ServiceSid);
            ServiceSetupRecovery.Save(root, journal.Binding, journal.Permissions, identities);
            checkpoint?.Invoke("identities-saved");
            pending = pending with { Ready = true }; Write(root, pending);
            checkpoint?.Invoke("ready");
            Finish(root, binding, pending);
        }
        catch
        {
            if (!pending.Ready) RollBack(root, binding, pending);
            throw;
        }
    }

    internal static void Recover(string installation, string? expectedWorkId = null, bool restoreCompleted = false)
    {
        ServiceRegistration.RequireAdministrator();
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        ServiceRetirement.RequireNotRetiring(root);
        var binding = ServiceWorkerBinding.Load(root);
        using var setup = StateFileLock.Acquire(ServicePortableLayout.Record(root, "activation"));
        var pendingExists = IsPending(root);
        if (!pendingExists && !restoreCompleted) return;
        using var operations = ServiceCoordinationFiles.TryOpen(new(root, binding), ServiceCoordinationFiles.OperationFile, true)
            ?? throw new IOException("Finish the current operation before recovering the update.");
        using var runtime = WindowsUpdateRuntime.TryExclusive(root)
            ?? throw new IOException("Close the other Backspace process before recovering the update.");
        var saved = ReadTrusted<Pending>(root, pendingExists ? FileName : LastFileName);
        if (saved.Version != 1 || saved.ServiceName != binding.ServiceName) throw new SecurityException("The update belongs to another service.");
        ValidatePlan(saved.Plan, root); ServiceSetupRecovery.Validate(saved.OriginalPermissions, root);
        if (saved.OriginalPermissions.Binding.ServiceName != binding.ServiceName || saved.OriginalPermissions.Binding.OwnerSid != binding.OwnerSid)
            throw new SecurityException("The update permissions belong to another service.");
        if (expectedWorkId is not null && (!Guid.TryParseExact(expectedWorkId, "N", out _) || expectedWorkId != Path.GetFileName(saved.Plan.Work)))
            throw new SecurityException("A newer update has replaced this recovery record. No application files were changed.");
        // Plan state changes after each replacement stage. Read the protected
        // file journal, not the earlier embedded Prepared snapshot.
        var current = WindowsUpdateRuntime.ReadPlan(Path.Combine(saved.Plan.Work, "plan.json"));
        if (current.Root != saved.Plan.Root || current.Work != saved.Plan.Work || current.Version != saved.Plan.Version
            || !current.Files.SequenceEqual(saved.Plan.Files)) throw new SecurityException("The update file journal changed unexpectedly.");
        saved = saved with { Plan = current };
        if (restoreCompleted)
        {
            if (!pendingExists && current.State == "RolledBack") return;
            saved = saved with { Ready = false };
            Write(root, saved); // Persist explicit rollback intent before disabling an installed version.
        }
        if (saved.Ready)
        {
            ServiceInstallationSecurity.ValidateCodeTree(root, binding.DataDirectory, binding.ServiceSid);
            var journal = ReadTrusted<ServiceSetupRecovery.Journal>(root, ServiceSetupRecovery.FileName);
            ServiceSetupRecovery.Validate(journal, root); ValidateOriginalIdentities(root, journal);
            Finish(root, binding, saved);
        }
        else RollBack(root, binding, saved);
    }

    private static void RollBack(string root, ServiceWorkerBinding binding, Pending saved)
    {
        ServiceScheduleRegistration.DisableOrRemove(binding, Path.Combine(root, "Backspace.exe"), remove: false);
        ServiceRegistration.QuiesceForSetupRecovery(binding, Path.Combine(root, "Backspace.exe"));
        if (saved.Plan.State is "Installing" or "Installed") WindowsUpdatePackage.RollBack(saved.Plan);
        ValidateOriginalIdentities(root, saved.OriginalPermissions);
        ServiceSetupRecovery.Save(root, saved.OriginalPermissions.Binding, saved.OriginalPermissions.Permissions, saved.OriginalPermissions.Identities);
        ServiceInstallationSecurity.ValidateCodeTree(root, binding.DataDirectory, binding.ServiceSid);
        Finish(root, binding, saved);
    }

    private static void Finish(string root, ServiceWorkerBinding binding, Pending saved)
    {
        var executable = Path.Combine(root, "Backspace.exe");
        var approved = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, binding.OwnerSid).Snapshot();
        ServiceRegistration.VerifyInstalled(binding, executable);
        ServiceScheduleRegistration.Enable(binding, executable, approved);
        ServiceRegistration.SetStartMode(binding, executable, automatic: true);
        ServiceSetup.WriteRecord(root, LastFileName, JsonSerializer.SerializeToUtf8Bytes(saved, ServiceRunAuthorization.Json));
        File.Delete(ServicePortableLayout.Record(root, FileName));
        // Keep the immediately previous bundle and its recovery executable.
        // Earlier completed attempts are now redundant; unfinished or unknown
        // work is retained. Cleanup cannot turn a verified update into failure.
        try
        {
            var current = WindowsUpdateRuntime.ReadPlan(Path.Combine(saved.Plan.Work, "plan.json"));
            ValidatePlan(current, root);
            WindowsUpdateRuntime.PruneOlderCompletedUpdates(current,
                error => AppLogger.For<Pending>().Warning(error, "An older completed update could not be removed"));
        }
        catch (Exception ex) { AppLogger.For<Pending>().Warning(ex, "Completed update retained its recovery files; cleanup will need another attempt"); }
    }

    internal static void ValidatePlan(WindowsUpdatePackage.Plan plan, string root)
    {
        WindowsUpdatePackage.ValidateLocation(plan);
        if (!plan.ServiceUpdate || plan.Root != root || plan.ExeName != "Backspace.exe"
            || plan.Files.Count != ServiceSetup.ProgramFiles.Length
            || !plan.Files.Select(f => f.Name).Order(StringComparer.Ordinal).SequenceEqual(ServiceSetup.ProgramFiles.Order(StringComparer.Ordinal)))
            throw new SecurityException("The service update must contain exactly the portable application and its three engines.");
        ServiceInstallationSecurity.ValidateProtectedRecord(Path.Combine(plan.Work, "plan.json"), root);
    }

    internal static void RecoverFromPlan(string path, bool restoreCompleted)
    {
        ServiceRegistration.RequireAdministrator();
        var plan = WindowsUpdateRuntime.ReadPlan(path);
        ValidatePlan(plan, plan.Root);
        Recover(plan.Root, Path.GetFileName(plan.Work), restoreCompleted);
    }

    internal static WindowsUpdatePackage.Plan PendingPlan(string root)
    {
        var saved = ReadTrusted<Pending>(root, FileName);
        var binding = ServiceWorkerBinding.LoadForOwner(root);
        if (saved.Version != 1 || saved.ServiceName != binding.ServiceName) throw new SecurityException("The update belongs to another service.");
        ValidatePlan(saved.Plan, root);
        return saved.Plan;
    }

    private static void WriteRecoveryLauncher(WindowsUpdatePackage.Plan plan)
    {
        WindowsUpdateRuntime.WriteRecoveryLauncher(plan);
        ServiceInstallationSecurity.ValidateProtectedRecord(Path.Combine(plan.Work, "recovery-runner.exe"), plan.Root);
    }

    private static void ValidateOriginalIdentities(string root, ServiceSetupRecovery.Journal journal)
    {
        foreach (var permission in journal.Permissions)
        {
            var isDirectory = permission.RelativePath is "." or AppPaths.AppFolderName;
            using var held = new ServiceObjectPermissions(Path.Combine(root, permission.RelativePath), isDirectory, workingFolder: isDirectory);
            if (held.Identity != journal.Identities[permission.RelativePath])
                throw new SecurityException("A protected file changed outside this update: " + permission.RelativePath);
        }
    }

    private static string CreateWork(string root)
    {
        var parent = Path.Combine(ServicePortableLayout.Control(root), "updates");
        const string acl = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FRFX;;;BU)";
        if (!Directory.Exists(parent)) ServiceSetup.CreateProtectedDirectory(parent, acl);
        ServiceInstallationSecurity.ValidateAncestors(parent, root);
        var work = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        ServiceSetup.CreateProtectedDirectory(work, acl);
        return work;
    }
    private static T ReadTrusted<T>(string root, string name)
    {
        var path = ServicePortableLayout.Record(root, name);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ServiceInstallationSecurity.ValidateProtectedRecord(path, root);
        if (file.Length is <= 0 or > 262144) throw new InvalidDataException("Invalid application update record.");
        return JsonSerializer.Deserialize<T>(file, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
    }
    private static void Write(string root, Pending pending)
        => ServiceSetup.WriteRecord(root, FileName, JsonSerializer.SerializeToUtf8Bytes(pending, ServiceRunAuthorization.Json));
}
