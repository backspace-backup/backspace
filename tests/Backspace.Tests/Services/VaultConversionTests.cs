using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Backspace.Models;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// The one-time move of an existing install into the portable vault (U153):
/// the owner's desktop secrets.bin, an activated service whose own vault holds
/// the authoritative sign-ins, and engine folders inside Backspace.config.
/// Each stage can be interrupted and resumed; nothing old is deleted before
/// vault.bin and the service's confirmation; no value reaches the logs.
/// </summary>
public sealed class VaultConversionTests
{
    private const string Password = "move-me";
    private const string LocalOnly = "local-only-password-1111";
    private const string ServiceToken = "service-wins-2222";
    private const string StaleLocal = "stale-local-3333";

    private static string Token(string expiry) => $"{{\"access_token\":\"tok\",\"refresh_token\":\"r\",\"expiry\":\"{expiry}\"}}";

    private sealed class FakeHost(string serviceDirectory) : IVaultConversionHost
    {
        public bool FailFreeze, FailUnlock, Ran;
        public int Freezes, Unlocks, Thaws;
        public bool HasService => true;
        public string? ServiceDataDirectory => serviceDirectory;
        public string? ServiceWorkRoot => Path.Combine(EngineWorkPaths.MachineRoot, "engine", EngineWorkPaths.ConfigHash(serviceDirectory));
        public IReadOnlyDictionary<string, string>? FreezeAndExport()
        {
            Freezes++;
            if (FailFreeze) throw new IOException("A backup is running in the background.");
            return new LegacySecretsVault(new DpapiSecretsEncryption(), Path.Combine(serviceDirectory, "secrets.bin")).ExportForMigration();
        }
        public string? Unlock(SecretsStore secrets)
        {
            Unlocks++;
            if (FailUnlock) return "the service is not running";
            // What the real service checks before taking its key copy.
            return ServiceVaultUnlock.Covers(secrets.Snapshot(),
                new LegacySecretsVault(new DpapiSecretsEncryption(), Path.Combine(serviceDirectory, "secrets.bin")).ExportForMigration())
                ? null : "vault-key-or-contents-rejected";
        }
        public void Thaw() => Thaws++;
        public bool RanOnVault(string vaultId, DateTime since) => Ran;
    }

    private sealed record Install(TempWorkspace Workspace, FakeHost Host, AppConfig Settings, string Service);

    private static Install OldInstall()
    {
        var ws = new TempWorkspace(vault: false);
        Environment.SetEnvironmentVariable(EngineWorkPaths.MachineRootVariable, ws.Sub("machine"));
        var config = ws.ConfigRoot;
        var service = Path.Combine(config, "service");
        Directory.CreateDirectory(service);
        var local = new LegacySecretsVault(new DpapiSecretsEncryption(), AppPaths.LegacySecretsFile);
        local.Set("dest:local:storagepw", LocalOnly);
        local.Set("dest:shared:storagepw", StaleLocal);
        local.Set("dest:cloud:oauth", Token("2026-01-01T00:00:00Z"));
        var serviceVault = new LegacySecretsVault(new DpapiSecretsEncryption(), Path.Combine(service, "secrets.bin"));
        serviceVault.Set("dest:shared:storagepw", ServiceToken);
        serviceVault.Set("dest:cloud:oauth", Token("2026-02-01T00:00:00Z"));
        // Engine folders the earlier versions kept inside Backspace.config.
        Directory.CreateDirectory(Path.Combine(config, "backrest", "oplog"));
        File.WriteAllText(Path.Combine(config, "backrest", "config.json"), "{\"repos\":[{\"password\":\"" + LocalOnly + "\"}]}");
        File.WriteAllText(Path.Combine(config, "backrest", "oplog", "ops.sqlite"), "history");
        Directory.CreateDirectory(Path.Combine(service, "backrest"));
        File.WriteAllText(Path.Combine(service, "backrest", "config.json"), "{\"repos\":[]}");
        var cloud = new Destination { Id = "cloud", Name = "Cloud", Kind = DestinationKind.DropboxAppScoped, OAuthTokenRef = "dest:cloud:oauth", PathOrSubpath = "x" };
        Directory.CreateDirectory(Path.Combine(service, "rclone-remotes"));
        // rclone refreshed the sign-in after the service vault last saw it.
        File.WriteAllText(Path.Combine(service, "rclone-remotes", ResticUriResolver.RcloneRemoteNameFor(cloud) + ".conf"),
            $"[{ResticUriResolver.RcloneRemoteNameFor(cloud)}]\ntype = dropbox\ntoken = {Token("2026-03-01T00:00:00Z")}\n");
        File.WriteAllText(Path.Combine(config, "rclone.conf"), "[other]\ntype = drive\n");
        Directory.CreateDirectory(Path.Combine(config, "engine-runs", "scheduled"));
        Directory.CreateDirectory(Path.Combine(service, "scratch"));
        var settings = new AppConfig();
        settings.Destinations.Add(cloud);
        return new Install(ws, new FakeHost(service), settings, service);
    }

    private static void Cleanup(Install install)
    {
        Environment.SetEnvironmentVariable(EngineWorkPaths.MachineRootVariable, null);
        install.Workspace.Dispose();
    }

    /// <summary>The owner's install, 8 November 2026: local_c succeeded on the
    /// new vault, then the service's last run was local_dropbox, skipped
    /// because its drive wasn't attached. The old files must still go.</summary>
    [Fact]
    public void The_old_files_go_after_any_successful_run_even_if_the_last_one_was_skipped()
    {
        var install = OldInstall();
        try
        {
            var secrets = new SecretsStore();
            Assert.Null(VaultConversion.Run(Password, secrets, install.Host, install.Settings));
            Assert.Equal("awaiting-first-run", VaultConversion.Read(AppPaths.ConfigRoot)!.Stage);
            install.Host.Ran = false; // the service's last run report: a skipped backup
            var backup = new Backup { Name = "local_c" };
            ServiceLocator.Config.Update(c => c.Backups.Add(backup));
            Assert.Null(VaultConversion.Continue(secrets, install.Host));
            Assert.True(File.Exists(Path.Combine(install.Service, "secrets.bin"))); // nothing succeeded yet

            ServiceLocator.Logs.AppendRun(new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Skipped,
                StartedUtc = DateTime.UtcNow.AddMinutes(-1), EndedUtc = DateTime.UtcNow });
            Assert.Null(VaultConversion.Continue(secrets, install.Host));
            Assert.True(File.Exists(Path.Combine(install.Service, "secrets.bin"))); // a skipped run proves nothing

            ServiceLocator.Logs.AppendRun(new RunRecord { BackupId = backup.Id, BackupName = backup.Name, Status = BackupRunStatus.Success,
                StartedUtc = DateTime.UtcNow.AddMinutes(-1), EndedUtc = DateTime.UtcNow.AddSeconds(1) });
            Assert.Null(VaultConversion.Continue(secrets, install.Host));
            Assert.Empty(Directory.GetFiles(AppPaths.ConfigRoot, "secrets.bin*"));
            Assert.Empty(Directory.GetFiles(install.Service, "secrets.bin*"));
            Assert.Null(VaultConversion.Read(AppPaths.ConfigRoot));
        }
        finally { Cleanup(install); }
    }

    [Fact]
    public void Converts_merges_takes_newer_tokens_moves_engine_folders_and_removes_old_files_after_the_first_run()
    {
        var install = OldInstall();
        try
        {
            var secrets = new SecretsStore();
            Assert.True(VaultConversion.Needed(AppPaths.ConfigRoot, install.Host));
            Assert.Null(VaultConversion.Run(Password, secrets, install.Host, install.Settings));

            // A new computer with the password sees the merged credentials.
            var elsewhere = new SecretsStore(AppPaths.VaultFile, new VaultKeyCache(install.Workspace.Sub("pc-b"), "user", new DpapiSecretsEncryption()));
            Assert.True(elsewhere.TryUnlock(Password));
            Assert.Equal(LocalOnly, elsewhere.Get("dest:local:storagepw"));
            Assert.Equal(ServiceToken, elsewhere.Get("dest:shared:storagepw"));
            Assert.Equal(Token("2026-03-01T00:00:00Z"), elsewhere.Get("dest:cloud:oauth"));
            Assert.Equal((1, 1, 1), (install.Host.Freezes, install.Host.Unlocks, install.Host.Thaws));

            var record = VaultConversion.Read(AppPaths.ConfigRoot)!;
            Assert.Equal("awaiting-first-run", record.Stage);
            Assert.Equal(3, record.Count);
            Assert.DoesNotContain(LocalOnly, File.ReadAllText(VaultConversion.RecordPath(AppPaths.ConfigRoot)));

            // Engine folders moved to this computer; readable leftovers gone.
            Assert.False(Directory.Exists(Path.Combine(AppPaths.ConfigRoot, "backrest")));
            Assert.Equal("history", File.ReadAllText(Path.Combine(EngineWorkPaths.RootFor(AppPaths.ConfigRoot), "backrest", "oplog", "ops.sqlite")));
            Assert.True(File.Exists(Path.Combine(install.Host.ServiceWorkRoot!, "backrest", "config.json")));
            Assert.False(Directory.Exists(Path.Combine(install.Service, "backrest")));
            Assert.False(Directory.Exists(Path.Combine(install.Service, "rclone-remotes")));
            Assert.False(File.Exists(Path.Combine(AppPaths.ConfigRoot, "rclone.conf")));
            Assert.False(Directory.Exists(Path.Combine(AppPaths.ConfigRoot, "engine-runs")));
            Assert.False(Directory.Exists(Path.Combine(install.Service, "scratch")));

            // The earlier vaults stay until a run used the new one.
            Assert.True(File.Exists(AppPaths.LegacySecretsFile));
            Assert.Null(VaultConversion.Continue(secrets, install.Host));
            Assert.True(File.Exists(Path.Combine(install.Service, "secrets.bin")));
            install.Host.Ran = true;
            Assert.Null(VaultConversion.Continue(secrets, install.Host));
            Assert.Empty(Directory.GetFiles(AppPaths.ConfigRoot, "secrets.bin*"));
            Assert.Empty(Directory.GetFiles(install.Service, "secrets.bin*"));
            Assert.Null(VaultConversion.Read(AppPaths.ConfigRoot));

            // Falsifiable: no value is readable anywhere under the configuration.
            foreach (var file in Directory.EnumerateFiles(AppPaths.ConfigRoot, "*", SearchOption.AllDirectories))
            {
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                Assert.DoesNotContain(LocalOnly, text);
                Assert.DoesNotContain(ServiceToken, text);
            }
        }
        finally { Cleanup(install); }
    }

    [Fact]
    public void A_service_busy_at_the_start_changes_nothing_and_a_retry_completes()
    {
        var install = OldInstall();
        try
        {
            install.Host.FailFreeze = true;
            var secrets = new SecretsStore();
            var error = VaultConversion.Run(Password, secrets, install.Host, install.Settings);
            Assert.Contains("Nothing was moved", error);
            Assert.False(File.Exists(AppPaths.VaultFile));
            Assert.True(Directory.Exists(Path.Combine(AppPaths.ConfigRoot, "backrest")));
            Assert.Equal(LocalOnly, new LegacySecretsVault(new DpapiSecretsEncryption(), AppPaths.LegacySecretsFile).Get("dest:local:storagepw"));
            Assert.Equal("started", VaultConversion.Read(AppPaths.ConfigRoot)!.Stage);

            install.Host.FailFreeze = false;
            Assert.Null(VaultConversion.Run(Password, secrets, install.Host, install.Settings));
            Assert.Equal("awaiting-first-run", VaultConversion.Read(AppPaths.ConfigRoot)!.Stage);
        }
        finally { Cleanup(install); }
    }

    [Fact]
    public void A_service_that_cannot_confirm_keeps_every_old_file_and_the_next_start_finishes()
    {
        var install = OldInstall();
        try
        {
            install.Host.FailUnlock = true;
            var secrets = new SecretsStore();
            Assert.NotNull(VaultConversion.Run(Password, secrets, install.Host, install.Settings));
            Assert.Equal("vault-written", VaultConversion.Read(AppPaths.ConfigRoot)!.Stage);
            Assert.True(File.Exists(AppPaths.VaultFile));
            Assert.True(Directory.Exists(Path.Combine(AppPaths.ConfigRoot, "backrest")));
            Assert.True(Directory.Exists(Path.Combine(install.Service, "rclone-remotes")));
            Assert.True(File.Exists(AppPaths.LegacySecretsFile));
            Assert.Equal(1, install.Host.Thaws);

            // The next start (vault open with this computer's key) carries on.
            install.Host.FailUnlock = false;
            Assert.Null(VaultConversion.Continue(new SecretsStore(), install.Host));
            Assert.Equal("awaiting-first-run", VaultConversion.Read(AppPaths.ConfigRoot)!.Stage);
            Assert.False(Directory.Exists(Path.Combine(install.Service, "rclone-remotes")));
        }
        finally { Cleanup(install); }
    }

    [Fact]
    public void A_vault_published_before_its_record_resumes_with_the_password_and_refuses_a_wrong_one()
    {
        var install = OldInstall();
        try
        {
            install.Host.FailUnlock = true;
            Assert.NotNull(VaultConversion.Run(Password, new SecretsStore(), install.Host, install.Settings));
            var vault = File.ReadAllBytes(AppPaths.VaultFile);
            // Interrupted between publishing vault.bin and saving its stage,
            // on a computer without the key copy yet.
            File.WriteAllText(VaultConversion.RecordPath(AppPaths.ConfigRoot), "{\"Version\":1,\"Stage\":\"started\"}");
            Directory.Delete(EngineWorkPaths.KeysDirectory, recursive: true);
            install.Host.FailUnlock = false;
            Assert.Equal(VaultGate.WrongPassword, VaultConversion.Run("not it", new SecretsStore(), install.Host, install.Settings));
            Assert.Equal(vault, File.ReadAllBytes(AppPaths.VaultFile));
            Assert.Null(VaultConversion.Run(Password, new SecretsStore(), install.Host, install.Settings));
            Assert.Equal(vault, File.ReadAllBytes(AppPaths.VaultFile));
            Assert.Equal("awaiting-first-run", VaultConversion.Read(AppPaths.ConfigRoot)!.Stage);
        }
        finally { Cleanup(install); }
    }

    [Fact]
    public void An_unreadable_earlier_vault_stops_before_anything_is_written()
    {
        var install = OldInstall();
        try
        {
            var garbage = new byte[] { 9, 9, 9, 9 };
            File.WriteAllBytes(AppPaths.LegacySecretsFile, garbage);
            var error = VaultConversion.Run(Password, new SecretsStore(), install.Host, install.Settings);
            Assert.Contains("nothing was moved", error);
            Assert.False(File.Exists(AppPaths.VaultFile));
            Assert.Equal(garbage, File.ReadAllBytes(AppPaths.LegacySecretsFile));
            Assert.Equal(0, install.Host.Freezes);
        }
        finally { Cleanup(install); }
    }

    [Fact]
    public void A_service_vault_missing_an_entry_is_refused_by_the_check()
    {
        var vault = new Dictionary<string, string> { ["a"] = "1", ["t"] = Token("2026-03-01T00:00:00Z") };
        Assert.True(ServiceVaultUnlock.Covers(vault, new Dictionary<string, string> { ["a"] = "1", ["t"] = Token("2026-02-01T00:00:00Z") }));
        Assert.False(ServiceVaultUnlock.Covers(vault, new Dictionary<string, string> { ["b"] = "2" }));
        Assert.False(ServiceVaultUnlock.Covers(vault, new Dictionary<string, string> { ["a"] = "changed" }));
        Assert.False(ServiceVaultUnlock.Covers(vault, new Dictionary<string, string> { ["t"] = Token("2026-04-01T00:00:00Z") }));
    }

    [Fact]
    public void No_credential_value_appears_in_the_logs()
    {
        var install = OldInstall();
        var lines = new List<string>();
        try
        {
            using (AppLogger.CaptureForTests(line => { lock (lines) lines.Add(line); }))
            {
                install.Host.FailUnlock = true;
                VaultConversion.Run(Password, new SecretsStore(), install.Host, install.Settings);
                install.Host.FailUnlock = false;
                VaultConversion.Continue(new SecretsStore(), install.Host);
                install.Host.Ran = true;
                VaultConversion.Continue(new SecretsStore(), install.Host);
            }
            Assert.NotEmpty(lines);
            var all = string.Join("\n", lines);
            foreach (var secret in new[] { LocalOnly, ServiceToken, StaleLocal, Password, "refresh_token" })
                Assert.DoesNotContain(secret, all);
        }
        finally { Cleanup(install); }
    }
}
