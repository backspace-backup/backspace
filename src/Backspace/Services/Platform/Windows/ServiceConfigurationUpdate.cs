using System;
using System.IO;
using System.Linq;
using System.Security;
using System.ServiceProcess;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Resumable administrator approval of changed backup settings. All
/// records stay with the portable executable. An interrupted update blocks
/// new service work until its recorded configuration is fully prepared.</summary>
internal static class ServiceConfigurationUpdate
{
    internal const string FileName = "configuration-update.json";
    internal sealed record Pending(int Version, string ServiceName, ServiceRunAuthorization.Approval Approval, bool Ready = false);

    internal static void Apply(string installation, string? expectedDigest, Action<string>? checkpoint = null)
    {
        ServiceRegistration.RequireAdministrator();
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        ServiceRetirement.RequireNotRetiring(root);
        ServiceProgramUpdate.RequireNoPending(root);
        var binding = ServiceWorkerBinding.Load(root);
        if (!binding.Activated) throw new InvalidOperationException("Finish background backup activation before changing its settings.");
        var executable = Path.Combine(root, "Backspace.exe");
        ServiceRegistration.VerifyInstalled(binding, executable);
        ServiceInstallationSecurity.ValidateCodeTree(root, binding.DataDirectory, binding.ServiceSid);
        using var setup = StateFileLock.Acquire(ServicePortableLayout.Record(root, "activation"));
        ServiceProgramUpdate.RequireNoPending(root);
        using var operations = ServiceCoordinationFiles.TryOpen(new(root, binding), ServiceCoordinationFiles.OperationFile, true)
            ?? throw new ServiceBusyException("A backup, restore or cleanup is still running. Your settings are applied when it finishes.");
        var pending = Read(root, binding);
        if (pending is null && expectedDigest is null) return; // Retrying a completed recovery is harmless.
        if (pending is null)
        {
            var configPath = Path.Combine(ServicePortableLayout.Config(root), "config.json");
            using var configLock = StateFileLock.Acquire(configPath);
            using var source = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length is <= 0 or > ServiceRunAuthorization.MaximumApprovalBytes) throw new InvalidDataException("Invalid backup settings size.");
            ServiceConfigurationComparison.SkipByteOrderMark(source);
            var config = JsonSerializer.Deserialize<AppConfig>(source, ServiceRunAuthorization.Json) ?? throw new InvalidDataException("Missing backup settings.");
            if (expectedDigest is null || expectedDigest != ServiceRunAuthorization.Digest(config))
                throw new IOException("The backup settings changed while Windows approval was open. Save them again to review the current version.");
            var approvalBytes = ServiceRunAuthorization.CreateApproval(binding.OwnerSid, Guid.NewGuid().ToString("N"), config);
            var candidate = JsonSerializer.Deserialize<ServiceRunAuthorization.Approval>(approvalBytes, ServiceRunAuthorization.Json)!;
            foreach (var backup in config.Backups.Where(b => SchedulePlan.ServiceCanRun(b, config.Destinations)))
                _ = ServiceApprovedWorker.LocalPathsFor(new(candidate.Revision, backup, config, RunKind.Backup)).ToArray();
            _ = ServiceRepositoryPermissions.RepositoryRoots(root, config).ToArray();
            pending = new(1, binding.ServiceName, candidate);
            Write(root, pending); // Durable intent before touching existing schedules or grants.
            checkpoint?.Invoke("pending-saved");
        }
        else if (expectedDigest is not null && expectedDigest != pending.Approval.ConfigurationSha256)
            throw new IOException("An earlier background-settings update needs to finish first. The newer desktop settings were not replaced.");

        if (!pending.Ready)
        {
            ServiceRegistration.QuiesceForSetupRecovery(binding, executable);
            // A restarted host sees the pending record and refuses new work.
            // Gate/permission setup completes before publishing the new policy.
            ServiceScheduleRegistration.Prepare(binding, executable, pending.Approval);
            ServiceCoordinationFiles.Prepare(root, binding, pending.Approval.Configuration);
            // Remove obsolete grants first: a retained new destination may be
            // nested inside an old root. Revoking the ancestor after granting
            // the child would otherwise silently remove the child's access.
            ServiceRepositoryPermissions.RevokeUnused(root, binding, pending.Approval.Configuration);
            ServiceRepositoryPermissions.Grant(root, binding, pending.Approval);
            ServiceSetup.WriteRecord(root, "service-approval.json", JsonSerializer.SerializeToUtf8Bytes(pending.Approval, ServiceRunAuthorization.Json));
            checkpoint?.Invoke("policy-saved");
            pending = pending with { Ready = true };
            Write(root, pending);
            checkpoint?.Invoke("ready");
        }
        // Ready means the policy and permissions agree. A crash while enabling
        // schedules resumes here; no stale desktop file is read on recovery.
        var installed = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, binding.OwnerSid).Snapshot();
        if (installed.Revision != pending.Approval.Revision || installed.ConfigurationSha256 != pending.Approval.ConfigurationSha256)
            throw new SecurityException("The approved settings changed during recovery. No schedule was enabled.");
        ServiceScheduleRegistration.Enable(binding, executable, pending.Approval);
        ServiceRegistration.SetStartMode(binding, executable, automatic: true);
        checkpoint?.Invoke("enabled");
        Remove(ServicePortableLayout.Record(root, FileName));
    }

    /// <summary>The service just started and the owner's tray app both read
    /// this record. They share deletion; a check of its protection may still
    /// hold it for a moment. Deleting at once failed in 5 of 10 settings
    /// updates in the release VM check (31 October 2026).</summary>
    private static void Remove(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { File.Delete(path); return; }
            catch (IOException) when (attempt < 50) { System.Threading.Thread.Sleep(100); }
        }
    }

    internal static bool IsPending(string installation, ServiceWorkerBinding binding)
        => Read(installation, binding) is { Ready: false };

    /// <summary>Any unfinished update, including one whose policy is saved but
    /// whose schedule or service start was interrupted.</summary>
    internal static bool IsUnfinished(string installation, ServiceWorkerBinding binding)
        => Read(installation, binding) is not null;

    private static Pending? Read(string installation, ServiceWorkerBinding binding)
    {
        var path = ServicePortableLayout.Record(installation, FileName);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            ServiceInstallationSecurity.ValidateProtectedRecord(path, installation);
            if (file.Length is <= 0 or > 2 * ServiceRunAuthorization.MaximumApprovalBytes) throw new InvalidDataException("Invalid settings-update record.");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            return Parse(bytes, binding.ServiceName, binding.OwnerSid);
        }
        catch (FileNotFoundException) { return null; }
    }

    internal static Pending Parse(byte[] bytes, string serviceName, string ownerSid)
    {
        if (bytes.Length is <= 0 or > 2 * ServiceRunAuthorization.MaximumApprovalBytes) throw new InvalidDataException("Invalid settings-update record size.");
        ServiceRunAuthorization.RejectDuplicateProperties(bytes);
        var pending = JsonSerializer.Deserialize<Pending>(bytes, ServiceRunAuthorization.Json) ?? throw new InvalidDataException("Missing settings update.");
        if (pending.Version != 1 || pending.ServiceName != serviceName || pending.Approval is null || pending.Approval.OwnerSid != ownerSid)
            throw new SecurityException("The saved settings update belongs to a different service or account.");
        // Validate the complete approved configuration, including its digest
        // over the saved text, without trusting data merely because it
        // deserialized successfully. A record saved by an earlier version
        // stays valid after an update.
        using (var document = JsonDocument.Parse(bytes))
        {
            if (!document.RootElement.TryGetProperty("approval", out var approval) || approval.ValueKind != JsonValueKind.Object)
                throw new SecurityException("The saved settings update is inconsistent.");
            try { ServiceRunAuthorization.ParseApproval(System.Text.Encoding.UTF8.GetBytes(approval.GetRawText()), ownerSid); }
            catch (SecurityException ex) { throw new SecurityException("The saved settings update is inconsistent.", ex); }
        }
        return pending;
    }

    private static void Write(string installation, Pending pending)
        => ServiceSetup.WriteRecord(installation, FileName, JsonSerializer.SerializeToUtf8Bytes(pending, ServiceRunAuthorization.Json));
}
