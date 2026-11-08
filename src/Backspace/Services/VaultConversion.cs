using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>What the conversion needs from the background service; tests
/// replace it. Without a set-up version-3 service for this copy, every
/// member reports "no service".</summary>
internal interface IVaultConversionHost
{
    /// <summary>A version-3 service set up for this copy holds credentials.</summary>
    bool HasService { get; }
    /// <summary>The service's folder in Backspace.config (service\), or null.</summary>
    string? ServiceDataDirectory { get; }
    /// <summary>Where the service's engine files now belong on this computer.</summary>
    string? ServiceWorkRoot { get; }
    /// <summary>Stops the service's runs and returns its earlier vault (null
    /// when it has none). Throws when a run is in progress.</summary>
    IReadOnlyDictionary<string, string>? FreezeAndExport();
    /// <summary>Sends the vault key with the "holds every earlier entry"
    /// check. Null when the service accepted, else a plain explanation.</summary>
    string? Unlock(SecretsStore secrets);
    void Thaw();
    /// <summary>The service finished a successful run with this vault after
    /// <paramref name="since"/>.</summary>
    bool RanOnVault(string vaultId, DateTime since);
}

internal sealed class NoServiceHost : IVaultConversionHost
{
    public bool HasService => false;
    public string? ServiceDataDirectory => null;
    public string? ServiceWorkRoot => null;
    public IReadOnlyDictionary<string, string>? FreezeAndExport() => null;
    public string? Unlock(SecretsStore secrets) => null;
    public void Thaw() { }
    public bool RanOnVault(string vaultId, DateTime since) => false;
}

/// <summary>
/// The one-time move of an existing install's credentials into the portable
/// vault (U153). Before it, the desktop kept them in <c>secrets.bin</c>
/// (Windows-protected for one account on one computer) and an activated
/// version-3 service held the authoritative copy in <c>service\secrets.bin</c>;
/// the engines' readable working files sat inside Backspace.config.
///
/// <para>Stages, recorded in <c>Backspace.config\vault-conversion.json</c>
/// (stage, vault id, count and digest only, never a value), so an
/// interrupted conversion resumes where it stopped:</para>
/// <list type="number">
/// <item><c>started</c>: the service is frozen and returns its vault; the
/// service's copy wins, the local vault fills gaps, and newer cloud tokens
/// from every old rclone file are taken.</item>
/// <item><c>vault-written</c>: vault.bin written, reopened with the password,
/// compared and published; this computer's key copy saved.</item>
/// <item><c>service-unlocked</c>: the service confirmed vault.bin holds every
/// entry of its earlier vault, kept its key copy and was thawed.</item>
/// <item><c>engine-moved</c>: the old engine folders moved to this computer;
/// old rclone files, engine runs and service scratch deleted.</item>
/// <item><c>awaiting-first-run</c>: the earlier secrets.bin files are deleted
/// after the first successful run on vault.bin, then the record goes.</item>
/// </list>
/// <para>Nothing old is deleted before vault.bin and the service unlock are
/// verified, and a failure leaves every old file in place.</para>
/// </summary>
internal static partial class VaultConversion
{
    internal const string RecordFileName = "vault-conversion.json";

    internal sealed record Record(int Version, string Stage, string? VaultId = null, int Count = 0, string? Digest = null,
        DateTime? StartedUtc = null, DateTime? ConvertedUtc = null);

    internal static string RecordPath(string config) => Path.Combine(config, RecordFileName);

    internal static Record? Read(string config)
    {
        var path = RecordPath(config);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Record>(FileAccessRetry.Get(() => File.ReadAllBytes(path), path)); }
        catch (JsonException ex)
        {
            AppLogger.For(nameof(VaultConversion)).Warning(ex, "The conversion record could not be read; it starts again");
            return null;
        }
    }

    private static void Save(string config, Record record)
    {
        var path = RecordPath(config);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions { WriteIndented = true }));
        FileAccessRetry.Run(() => File.Move(temporary, path, overwrite: true), path);
        AppLogger.For(nameof(VaultConversion)).Information("Saved-password conversion: {Stage} ({Count} entries)", record.Stage, record.Count);
    }

    /// <summary>The earlier files are here and the portable vault isn't: ask
    /// once to choose a password.</summary>
    internal static bool Needed(string config, IVaultConversionHost host)
    {
        if (File.Exists(Path.Combine(config, VaultFile.FileName)) || File.Exists(Path.Combine(config, VaultFile.FileName + ".bak"))) return false;
        var legacy = Path.Combine(config, "secrets.bin");
        if (File.Exists(legacy) || File.Exists(legacy + ".bak")) return true;
        return host.ServiceDataDirectory is { } service && File.Exists(Path.Combine(service, "secrets.bin"));
    }

    /// <summary>Runs or resumes the conversion. Null when it has gone as far as
    /// it can now; otherwise a plain explanation (and every old file kept).</summary>
    internal static string? Run(string password, SecretsStore secrets, IVaultConversionHost host, AppConfig settings, string? config = null)
    {
        config ??= AppPaths.ConfigRoot;
        var log = AppLogger.For(nameof(VaultConversion));
        var record = Read(config);
        // A later stage on a computer without the key yet: the password typed
        // now opens the vault, then the move carries on.
        if (record is not null && record.Stage != "started" && File.Exists(secrets.FilePath)
            && secrets.Refresh() != VaultState.Unlocked && !secrets.TryUnlock(password))
            return secrets.State == VaultState.Unreadable ? secrets.LoadErrorMessage : VaultGate.WrongPassword;
        if (record is null || record.Stage == "started" || !File.Exists(secrets.FilePath))
        {
            if (File.Exists(secrets.FilePath))
            {
                // A vault published by an earlier attempt whose record was not
                // updated: carry on from there.
                if (secrets.Refresh() != VaultState.Unlocked && !secrets.TryUnlock(password))
                    return secrets.State == VaultState.Unreadable ? secrets.LoadErrorMessage : VaultGate.WrongPassword;
                record = Describe(secrets, "vault-written");
                Save(config, record);
            }
            else
            {
                record = new Record(1, "started", StartedUtc: DateTime.UtcNow);
                Save(config, record);
                Dictionary<string, string> local;
                try { local = File.Exists(AppPaths.LegacySecretsFile) || File.Exists(AppPaths.LegacySecretsFile + ".bak") ? new LegacySecretsVault().ExportForMigration() : new(); }
                catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
                {
                    log.Warning(ex, "The earlier saved passwords could not be opened; nothing was moved");
                    return "Backspace can't open your earlier saved passwords on this computer, so nothing was moved. " +
                        "Sign in to Windows with the account that set up these backups, then open Backspace again.";
                }
                IReadOnlyDictionary<string, string>? service;
                try { service = host.FreezeAndExport(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
                {
                    log.Warning(ex, "The background service could not return its saved passwords; nothing was moved");
                    return ex.Message.Length > 0 ? ex.Message + " Nothing was moved." : "The background service did not answer. Nothing was moved; try again.";
                }
                try
                {
                    var map = Merge(local, service);
                    var imported = ImportNewerTokens(map, settings, OldRcloneFiles(config, host.ServiceDataDirectory));
                    log.Information("Saved-password conversion: {Local} local, {Service} from the service, {Tokens} newer cloud sign-ins, {Total} in all",
                        local.Count, service?.Count ?? 0, imported, map.Count);
                    secrets.CreateVerified(password, map);
                    record = Describe(secrets, "vault-written") with { StartedUtc = record.StartedUtc };
                    Save(config, record);
                }
                catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
                {
                    log.Warning(ex, "The new vault could not be written; nothing was moved");
                    try { host.Thaw(); } catch (Exception thaw) { log.Warning(thaw, "The service could not be thawed"); }
                    return "Your saved passwords could not be moved: " + ex.Message + " Your earlier files are unchanged.";
                }
            }
        }
        return Continue(secrets, host, config);
    }

    /// <summary>Continues a conversion whose vault is already written (at
    /// start, with the vault open). Null when done for now, else why not.</summary>
    internal static string? Continue(SecretsStore secrets, IVaultConversionHost host, string? config = null)
    {
        config ??= AppPaths.ConfigRoot;
        var log = AppLogger.For(nameof(VaultConversion));
        var record = Read(config);
        if (record is null) return null;
        if (record.Stage == "started") return VaultLockedException.ConversionMessage;
        if (secrets.Refresh() != VaultState.Unlocked) return VaultLockedException.LockedMessage;
        if (record.Stage == "vault-written")
        {
            if (host.HasService)
            {
                string? error;
                try { error = host.Unlock(secrets); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException)
                { error = ex.Message; }
                if (error is not null)
                {
                    log.Warning("The background service did not confirm the new vault: {Error}", error);
                    try { host.Thaw(); } catch (Exception ex) { log.Warning(ex, "The service could not be thawed"); }
                    return "The background service couldn't confirm your saved passwords yet (" + error + "). " +
                        "Backspace tries again at its next start; your earlier files are kept until then.";
                }
                host.Thaw();
            }
            record = record with { Stage = "service-unlocked" };
            Save(config, record);
        }
        if (record.Stage == "service-unlocked")
        {
            MoveEngineFolders(config, host);
            record = record with { Stage = "engine-moved" };
            Save(config, record);
        }
        if (record.Stage == "engine-moved")
        {
            record = record with { Stage = "awaiting-first-run", ConvertedUtc = DateTime.UtcNow };
            Save(config, record);
        }
        if (record.Stage == "awaiting-first-run") FinishAfterFirstRun(record, host, config);
        return null;
    }

    private static Record Describe(SecretsStore secrets, string stage)
    {
        var map = secrets.Snapshot();
        return new Record(1, stage, secrets.VaultId, map.Count, VaultFile.Digest(map));
    }

    /// <summary>The service's copy wins (it held the authoritative sign-ins);
    /// the local vault fills the gaps.</summary>
    internal static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> local, IReadOnlyDictionary<string, string>? service)
    {
        var map = new Dictionary<string, string>(local, StringComparer.Ordinal);
        if (service is not null) foreach (var (key, value) in service) map[key] = value;
        return map;
    }

    internal static IEnumerable<string> OldRcloneFiles(string config, string? serviceDirectory)
    {
        foreach (var root in new[] { config, serviceDirectory }.Where(r => r is not null))
        {
            var shared = Path.Combine(root!, "rclone.conf");
            if (File.Exists(shared)) yield return shared;
            var remotes = Path.Combine(root!, "rclone-remotes");
            if (Directory.Exists(remotes))
                foreach (var file in Directory.EnumerateFiles(remotes, "*.conf")) yield return file;
        }
    }

    /// <summary>rclone may have refreshed a sign-in into its own file after
    /// the vault last saw it. The newest token wins. Returns how many were taken.</summary>
    internal static int ImportNewerTokens(Dictionary<string, string> map, AppConfig settings, IEnumerable<string> files)
    {
        var count = 0;
        var sections = new List<Dictionary<string, string>>();
        foreach (var file in files)
        {
            try { sections.Add(RcloneConfigBuilder.ParseSections(FileAccessRetry.Get(() => File.ReadAllText(file, Encoding.UTF8), file))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { AppLogger.For(nameof(VaultConversion)).Warning(ex, "An old rclone file could not be read: {File}", Path.GetFileName(file)); }
        }
        foreach (var destination in settings.Destinations.Where(d => !string.IsNullOrEmpty(d.OAuthTokenRef) && RcloneConfigBuilder.NeedsRcloneRemote(d)))
        {
            var remote = ResticUriResolver.RcloneRemoteNameFor(destination);
            foreach (var parsed in sections)
            {
                if (!parsed.TryGetValue(remote, out var section)) continue;
                var line = section.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.IndexOf('=') is var e && e > 0 && l[..e].Trim() == "token");
                if (line is null) continue;
                var token = line[(line.IndexOf('=') + 1)..].Trim();
                var known = map.TryGetValue(destination.OAuthTokenRef!, out var current);
                if (!known ? token.StartsWith('{') : RcloneConfigBuilder.IsNewerToken(token, current!))
                {
                    map[destination.OAuthTokenRef!] = token;
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>Old engine folders inside Backspace.config move to this
    /// computer; files that held readable credentials and can be rebuilt go.</summary>
    internal static void MoveEngineFolders(string config, IVaultConversionHost host)
    {
        MoveBackrest(Path.Combine(config, "backrest"), EngineWorkPaths.RootFor(config));
        if (host.ServiceDataDirectory is { } service && host.ServiceWorkRoot is { } serviceRoot)
            MoveBackrest(Path.Combine(service, "backrest"), serviceRoot);
        foreach (var root in new[] { config, host.ServiceDataDirectory }.Where(r => r is not null))
        {
            DeleteFile(Path.Combine(root!, "rclone.conf"));
            foreach (var name in new[] { "rclone-remotes", "engine-runs", "scratch" })
                DeleteTree(Path.Combine(root!, name));
        }
    }

    /// <summary>Under the old folder's sidecar owner lock, so no engine
    /// started by an earlier version still works there. When this computer's
    /// folder already exists (rebuilt meanwhile) the old one is just removed.</summary>
    private static void MoveBackrest(string old, string workRoot)
    {
        if (!Directory.Exists(old)) return;
        var target = Path.Combine(workRoot, "backrest");
        var log = AppLogger.For(nameof(VaultConversion));
        using (BackrestSidecar.AcquireDataDirectoryLease(old))
        {
            if (!Directory.Exists(target) || !Directory.EnumerateFileSystemEntries(target).Any())
            {
                Directory.CreateDirectory(target);
                CopyTree(old, target);
                log.Information("The engine's working files moved to this computer");
            }
        }
        DeleteTree(old);
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var child = Path.Combine(target, Path.GetFileName(directory));
            Directory.CreateDirectory(child);
            CopyTree(directory, child);
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (Path.GetFileName(file).Equals("sidecar.owner.lock", StringComparison.OrdinalIgnoreCase)) continue;
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            FileAccessRetry.Run(() => File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false), file);
        }
    }

    internal static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindows()) { Platform.Windows.SafeTreeDelete.Delete(path); return; }
#endif
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLogger.For(nameof(VaultConversion)).Warning(ex, "An old engine folder could not be removed yet: {Name}", Path.GetFileName(path)); }
    }

    private static void DeleteFile(string path)
    {
        try { FileAccessRetry.DeleteIfExists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { AppLogger.For(nameof(VaultConversion)).Warning(ex, "An old file could not be removed yet: {Name}", Path.GetFileName(path)); }
    }

    /// <summary>The earlier secrets.bin files go only once a run has used the
    /// new vault successfully: the service's when there is one, else this
    /// app's own.</summary>
    /// <summary>A backup succeeded after <paramref name="since"/>, in the run
    /// history (this app's and the background service's). Runs after the
    /// conversion can only have used the new vault.</summary>
    private static bool SucceededSince(DateTime since)
    {
        if (!ServiceLocator.Initialized) return false;
        try
        {
            return ServiceLocator.Config.Current.Backups.Any(b => ServiceLocator.Logs.LoadHistoryByBackupId(b.Id)
                .Any(r => r.Kind == RunKind.Backup && r.Status == BackupRunStatus.Success && r.EndedUtc > since));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLogger.For(nameof(VaultConversion)).Warning(ex, "Run history unavailable; the earlier saved-password files are kept for now");
            return false;
        }
    }

    private static void FinishAfterFirstRun(Record record, IVaultConversionHost host, string config)
    {
        var since = record.ConvertedUtc ?? DateTime.UtcNow;
        // Any successful backup since the conversion counts, not only the
        // service's very last run: on the owner's install local_c succeeded on
        // the new vault, then local_dropbox was skipped (its drive wasn't
        // attached), and the old files stayed (8 November 2026).
        var ran = host.HasService
            ? record.VaultId is { } id && host.RanOnVault(id, since) || SucceededSince(since)
            : ServiceLocator.Initialized && ServiceLocator.Config.Current.Backups.Any(b => b.LastSuccessUtc > since);
        if (!ran) return;
        foreach (var root in new[] { config, host.ServiceDataDirectory }.Where(r => r is not null))
            foreach (var file in Directory.EnumerateFiles(root!, "secrets.bin*").Concat(Directory.EnumerateFiles(root!, "secrets-key.bin*")))
                DeleteFile(file);
        DeleteFile(RecordPath(config));
        AppLogger.For(nameof(VaultConversion)).Information("Saved-password conversion finished; the earlier per-computer files were removed");
    }

    /// <summary>The host for this copy: its version-3 service when set up.</summary>
    internal static IVaultConversionHost HostForThisCopy()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows() && Platform.Windows.WindowsVaultConversionHost.ForThisCopy() is { } host) return host;
#endif
        return new NoServiceHost();
    }

    /// <summary>Tests replace the host.</summary>
    internal static Func<IVaultConversionHost>? HostOverride { get; set; }
    internal static IVaultConversionHost Host() => HostOverride?.Invoke() ?? HostForThisCopy();
}
