using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Backspace.Services.Platform.Windows;

/// <summary>The conversion's view of this copy's version-3 service (U153):
/// freeze it and take its earlier vault back over the existing return pipe,
/// send it the vault key with the "holds every earlier entry" check, thaw
/// it, and read its last run's report.</summary>
internal sealed class WindowsVaultConversionHost(SystemService.Registration system) : IVaultConversionHost
{
    private string? _returnId;

    internal static WindowsVaultConversionHost? ForThisCopy()
    {
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        if (!ServicePortableLayout.IsPortableConfig(root, AppPaths.ConfigRoot)) return null;
        return SystemService.ForCopy(root) is { Activated: true } system ? new WindowsVaultConversionHost(system) : null;
    }

    public bool HasService => true;
    public string? ServiceDataDirectory => system.DataDirectory;

    /// <summary>The service's machine root is its engines' parent folder, the
    /// owner's %LOCALAPPDATA%\Backspace (SystemBackupService.RunInstalled).</summary>
    public string? ServiceWorkRoot => system.EngineRoot.Length == 0 ? null
        : Path.Combine(Path.GetDirectoryName(system.EngineRoot.TrimEnd('\\'))!, "engine", EngineWorkPaths.ConfigHash(system.DataDirectory));

    public IReadOnlyDictionary<string, string>? FreezeAndExport()
    {
        if (!File.Exists(ServiceVaultUnlock.LegacyVaultPath(system.DataDirectory))) return null;
        SystemService.Start(system.ServiceName, TimeSpan.FromSeconds(60));
        _returnId = SystemServiceClient.Freeze(system);
        var reply = ServiceVaultReturnProtocol.Exchange(system.Binding, new(1, "export", _returnId));
        return reply.Secrets ?? throw new IOException("The background service did not return its saved passwords.");
    }

    public string? Unlock(SecretsStore secrets)
    {
        SystemService.Start(system.ServiceName, TimeSpan.FromSeconds(60));
        var error = ServiceVaultUnlock.Push(system, secrets, verifyLegacy: true);
        return error switch
        {
            null => null,
            "busy" => "a backup is running in the background",
            "vault-key-or-contents-rejected" => "it found a saved password missing from the new file",
            _ => error,
        };
    }

    public void Thaw()
    {
        if (SystemService.Status(system.ServiceName) == System.ServiceProcess.ServiceControllerStatus.Running)
            SystemServiceClient.Thaw(system);
        _returnId = null;
    }

    public bool RanOnVault(string vaultId, DateTime since)
    {
        var path = Path.Combine(system.DataDirectory, "last-service-run.json");
        try
        {
            if (!File.Exists(path)) return false;
            using var report = JsonDocument.Parse(FileAccessRetry.Get(() => File.ReadAllBytes(path), path));
            var root = report.RootElement;
            return root.TryGetProperty("vaultId", out var id) && id.GetString() == vaultId
                && root.TryGetProperty("finishedUtc", out var finished) && finished.GetDateTime() > since
                && root.TryGetProperty("run", out var run) && run.TryGetProperty("status", out var status)
                && (status.ValueKind == JsonValueKind.String ? status.GetString() == "Success" : status.GetInt32() == 0);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException)
        {
            AppLogger.For<WindowsVaultConversionHost>().Debug(ex, "The service's last run report could not be read");
            return false;
        }
    }
}
