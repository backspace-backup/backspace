using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// How the version-3 background service gets the vault key (U153). The
/// service opens the portable folder's <c>vault.bin</c> itself, with its own
/// copy of the vault key, protected by Windows for its account (LocalSystem)
/// in <c>%LOCALAPPDATA%\Backspace\keys\&lt;vault id&gt;.service.bin</c> of
/// the owner. The app sends that key over the owner's authenticated runs
/// pipe ("unlock") when it sets the service up, after the vault is created or
/// unlocked, and at start. The service accepts it only when it opens the
/// vault; a wrong key writes nothing.
/// </summary>
internal static class ServiceVaultUnlock
{
    internal sealed record Reply(int Version, string? State = null, string? Error = null);

    internal static string VaultPath(string installation) => Path.Combine(ServicePortableLayout.Config(installation), VaultFile.FileName);

    /// <summary>The service's own view of the portable vault.</summary>
    internal static SecretsStore ServiceVault(string installation, string machineRoot)
        => new(VaultPath(installation), VaultKeyCache.ForService(machineRoot));

    /// <summary>Run by the service. Returns null when accepted, else an error
    /// code.</summary>
    internal static string? Accept(string installation, string machineRoot, string? vaultId, byte[]? key)
    {
        var vaultPath = VaultPath(installation);
        if (vaultId is null || key is null)
        {
            // No credentials saved yet: nothing to unlock.
            if (File.Exists(vaultPath) || File.Exists(vaultPath + ".bak")) return "vault-key-missing";
            return null;
        }
        var vault = ServiceVault(installation, machineRoot);
        if (vault.TryUnlockWithKey(vaultId, key)) return null;
        return vault.State switch
        {
            VaultState.NoVault => "vault-missing",
            VaultState.Unreadable => "vault-unreadable",
            _ => "vault-key-rejected",
        };
    }

    // ---- desktop side ----

    /// <summary>Sends this computer's vault key to the copy's service. Returns
    /// the service's error code, or null when it accepted.</summary>
    internal static string? Push(SystemService.Registration system, SecretsStore secrets)
    {
        string? id = null;
        byte[]? key = null;
        if (secrets.Refresh() == VaultState.Unlocked) (id, key) = secrets.ExportKey();
        else if (secrets.State != VaultState.NoVault) throw new VaultLockedException(secrets.State, secrets.LoadErrorMessage);
        byte[]? payload = null;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, command = "unlock", vaultId = id,
                key = key is null ? null : Convert.ToBase64String(key),
            });
            var bytes = SendAsync(system, payload).GetAwaiter().GetResult();
            var reply = JsonSerializer.Deserialize<Reply>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new IOException("The background service did not answer.");
            return reply.Error;
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Best effort at start and after unlocking: the copy's service
    /// gets the key if it is set up. Failures are logged, never shown.</summary>
    internal static void PushIfServiceSetUp(SecretsStore secrets)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return;
            var root = AppContext.BaseDirectory.TrimEnd('\\');
            if (!ServicePortableLayout.IsPortableConfig(root, AppPaths.ConfigRoot)) return;
            if (SystemService.ForCopy(root) is not { Activated: true } system) return;
            if (secrets.Refresh() != VaultState.Unlocked) return;
            if (SystemService.Status(system.ServiceName) != System.ServiceProcess.ServiceControllerStatus.Running) return;
            var error = Push(system, secrets);
            if (error is not null) AppLogger.For(nameof(ServiceVaultUnlock)).Warning("The background service did not take the vault key: {Error}", error);
        }
        catch (Exception ex)
        {
            AppLogger.For(nameof(ServiceVaultUnlock)).Warning(ex, "The background service could not be given the vault key");
        }
    }

    private static async Task<byte[]> SendAsync(SystemService.Registration system, byte[] payload)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var pipe = await BackupServicePipe.ConnectAsync(system.ServiceName + "-runs", SystemService.LocalSystemSid, TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, payload, TimeSpan.FromSeconds(10), timeout.Token).ConfigureAwait(false);
        // PBKDF2 is not involved, but the service may wait for its vault file.
        return await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(45), timeout.Token).ConfigureAwait(false);
    }
}
