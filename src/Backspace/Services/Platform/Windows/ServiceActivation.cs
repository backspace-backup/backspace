using System;
using System.IO;
using System.Security;
using System.Text.Json;

namespace Backspace.Services.Platform.Windows;

internal static class ServiceActivation
{
    /// <summary>Left in the settings folder by the approved setup process once
    /// the service is prepared. The desktop then hands its saved sign-ins
    /// over and deletes it; a deleted hint tells the waiting process to stop.
    /// It only times the steps: every record is still validated.</summary>
    internal const string PreparedHintFile = "service-setup-prepared";

    /// <summary>
    /// One Windows approval for a new setup (U050): after preparing the
    /// service, the approved process waits here while the desktop, running as
    /// the owner, copies the saved sign-ins to the service, then activates.
    /// Two separate approvals asked a standard account for an administrator's
    /// password twice.
    /// </summary>
    internal static void ActivateAfterHandoff(string installation, TimeSpan timeout)
    {
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        var hint = Path.Combine(ServicePortableLayout.Config(root), PreparedHintFile);
        File.WriteAllText(hint, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var binding = ServiceWorkerBinding.Load(root);
            var grantPath = ServicePortableLayout.Record(root, ServiceMigrationProtocol.GrantFileName);
            ServiceInstallationSecurity.ValidateProtectedRecord(grantPath, root);
            var grant = ReadSmall<ServiceMigrationProtocol.Grant>(grantPath);
            var receipt = Path.Combine(binding.DataDirectory, "migration-" + grant.MigrationId + ".receipt.accepted");
            var deadline = DateTime.UtcNow + timeout;
            while (!File.Exists(receipt))
            {
                if (!File.Exists(hint))
                    throw new OperationCanceledException("Backspace stopped the setup before your saved sign-ins were handed over. Nothing was activated.");
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("Your saved sign-ins were not handed over in time. Turn administrator access on again to finish.");
                System.Threading.Thread.Sleep(250);
            }
            Activate(root);
        }
        finally { try { File.Delete(hint); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    internal static void Activate(string installation)
    {
        ServiceRegistration.RequireAdministrator();
        var root = ServiceInstallationSecurity.CanonicalLocalPath(installation);
        ServiceRetirement.RequireNotRetiring(root);
        var binding = ServiceWorkerBinding.Load(root);
        if (binding.Version != 2) throw new SecurityException("This service must be prepared by this version of Backspace.");
        var executable = Path.Combine(root, "Backspace.exe");
        ServiceRegistration.VerifyInstalled(binding, executable);
        ServiceInstallationSecurity.ValidateCodeTree(root, binding.DataDirectory, binding.ServiceSid);
        using var setupLock = StateFileLock.Acquire(ServicePortableLayout.Record(root, "activation"));
        ServiceProgramUpdate.RequireNoPending(root);
        using var originalOperations = binding.Activated ? null : ServiceCoordinationFiles.HoldOriginalOperations(root);
        var approval = ServiceRunAuthorization.Load(ServicePortableLayout.Record(root, "service-approval.json"), root, binding.OwnerSid).Snapshot();
        ServiceCoordinationFiles.Prepare(root, binding, approval.Configuration);
        using var migrationLease = ServiceCoordinationFiles.TryOpen(new(root, binding), ServiceCoordinationFiles.MigrationFile, true)
            ?? throw new IOException("The credential copy is being refreshed. Retry activation after it finishes.");
        var grantPath = ServicePortableLayout.Record(root, ServiceMigrationProtocol.GrantFileName);
        ServiceInstallationSecurity.ValidateProtectedRecord(grantPath, root);
        var grant = ReadSmall<ServiceMigrationProtocol.Grant>(grantPath);
        if (grant.Version != 1 || grant.OwnerSid != binding.OwnerSid || !Guid.TryParseExact(grant.MigrationId, "N", out _))
            throw new SecurityException("The credential handoff grant is invalid.");
        var accepted = ReadSmall<ServiceMigrationProtocol.Acceptance>(Path.Combine(binding.DataDirectory, "migration-" + grant.MigrationId + ".receipt.accepted"));
        if (accepted.Version != 1 || accepted.MigrationId != grant.MigrationId
            || !File.Exists(Path.Combine(binding.DataDirectory, "secrets.bin")))
            throw new SecurityException("The service's credential copy has not been verified and accepted.");

        ServiceLegacyTasks.Disable(root, binding);
        ServiceScheduleRegistration.Prepare(binding, executable, approval);
        ServiceCoordinationFiles.Prepare(root, binding, approval.Configuration);
        ServiceRepositoryPermissions.Grant(root, binding, approval);
        // This is the durable provider switch. Failures after this point must
        // recover forward; reverting to the old vault could lose newer tokens.
        if (!binding.Activated)
        {
            binding = binding with { Activated = true };
            ServiceSetup.WriteRecord(root, ServiceWorkerBinding.FileName, JsonSerializer.SerializeToUtf8Bytes(binding, ServiceRunAuthorization.Json));
        }
        ServiceRegistration.SetStartMode(binding, executable, automatic: true);
        ServiceScheduleRegistration.Enable(binding, executable, approval);
        ServiceLegacyTasks.Remove(root, binding);
    }

    private static T ReadSmall<T>(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > 8192) throw new InvalidDataException("A service activation record is invalid.");
        return JsonSerializer.Deserialize<T>(file, ServiceRunAuthorization.Json)
            ?? throw new InvalidDataException("A service activation record is missing.");
    }
}
