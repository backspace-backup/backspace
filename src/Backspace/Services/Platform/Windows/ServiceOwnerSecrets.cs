using System;
using System.IO;
using System.ServiceProcess;

namespace Backspace.Services.Platform.Windows;

/// <summary>Resolve the credential authority inside the original vault's
/// transaction. Existing desktop consumers therefore follow activation without
/// retaining a writable reference to an obsolete local vault.</summary>
internal sealed class ServiceOwnerSecrets(string installation, SecretsStore local) : ISecretsBackend
{
    private readonly string _vaultPath = Path.Combine(ServicePortableLayout.Config(installation), "secrets.bin");
    private bool _usingService;
    private string? _serviceName;
    public string? LoadErrorMessage => _usingService ? null : local.LoadErrorMessage;

    internal static bool Applies(string installation, string config)
        => string.Equals(Path.GetFullPath(config).TrimEnd('\\'), ServicePortableLayout.Config(installation), StringComparison.OrdinalIgnoreCase);

    private T WithAuthority<T>(Func<SecretsStore, T> action)
    {
        using var transaction = StateFileLock.Acquire(_vaultPath);
        if (ServiceRetirement.Read(installation) is { Stage: "complete" } retirement)
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity.User?.Value != retirement.Binding.OwnerSid)
                throw new System.Security.SecurityException("The returned vault belongs to another Windows account.");
            _usingService = false;
            return action(local);
        }
        var path = ServicePortableLayout.Record(installation, ServiceWorkerBinding.FileName);
        var exists = true;
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { exists = false; }
        catch (DirectoryNotFoundException) { exists = false; }
        if (!exists)
        {
            if (_usingService && !ReturnedToOwner()) throw new IOException("The active service registration is missing. The retained local vault was not used.");
            return action(local);
        }
        // Invalid or inaccessible registration is an error, never permission
        // to use the retained source vault. The owner is checked on every use.
        var binding = ServiceWorkerBinding.LoadForOwner(installation);
        if (!binding.Activated)
        {
            if (_usingService && !ReturnedToOwner()) throw new IOException("Service activation changed unexpectedly. The retained local vault was not used.");
            return action(local);
        }
        _usingService = true;
        _serviceName = binding.ServiceName;
        using var controller = new ServiceController(binding.ServiceName);
        ServiceRegistration.EnsureRunning(controller, TimeSpan.FromSeconds(20));
        return action(new SecretsStore(new ServiceSecretsBackend(binding.ServiceName + "-secrets", binding.ServiceSid)));
    }

    private bool ReturnedToOwner()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var returned = ServiceRetirement.HasCompletedArchive(installation, _serviceName, identity.User!.Value);
        if (returned) _usingService = false;
        return returned;
    }

    public void ValidateReadable() => WithAuthority(v => { v.ValidateReadable(); return true; });
    public bool TryGet(string key, out string value)
    {
        var result = WithAuthority(v => { var found = v.TryGet(key, out var text); return (found, text); });
        value = result.text; return result.found;
    }
    public void Set(string key, string value) => WithAuthority(v => { v.Set(key, value); return true; });
    public void Remove(string key) => WithAuthority(v => { v.Remove(key); return true; });
}
