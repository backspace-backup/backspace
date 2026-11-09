using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// U153: the background service opens the portable vault with its own key
/// copy, which it accepts from the app only when that key opens the vault;
/// the engine's readable working files live on this computer, not in the
/// synced folder.
/// </summary>
public sealed class VaultServiceAndPathsTests
{
    private static (string Installation, string Data, string Machine, SecretsStore App) Install(TempWorkspace ws)
    {
        var installation = ws.Sub("install");
        var data = Path.Combine(installation, AppPaths.AppFolderName, "service");
        Directory.CreateDirectory(data);
        var app = new SecretsStore(ServiceVaultUnlock.VaultPath(installation),
            new VaultKeyCache(ws.Sub("owner-keys"), "user", new DpapiSecretsEncryption()));
        app.Create("pw");
        app.Set("dest:1:storagepw", "repo-secret");
        return (installation, data, ws.Sub("machine"), app);
    }

    [Fact]
    public void The_service_refuses_a_wrong_key_and_keeps_nothing()
    {
        using var ws = new TempWorkspace();
        var (installation, data, machine, app) = Install(ws);
        var (id, _) = app.ExportKey();
        var wrong = new byte[VaultFile.KeyBytes];
        Assert.Equal("vault-key-rejected", ServiceVaultUnlock.Accept(installation, machine, id, wrong));
        Assert.Equal("vault-key-rejected", ServiceVaultUnlock.Accept(installation, machine, VaultFile.NewId(), app.ExportKey().Key));
        Assert.False(Directory.Exists(Path.Combine(machine, "keys")) && Directory.EnumerateFiles(Path.Combine(machine, "keys")).Any());
        Assert.Equal(VaultState.Locked, ServiceVaultUnlock.ServiceVault(installation, machine).Refresh());
    }

    [Fact]
    public void The_right_key_gives_the_service_its_own_copy_and_survives_a_password_change()
    {
        using var ws = new TempWorkspace();
        var (installation, data, machine, app) = Install(ws);
        var (id, key) = app.ExportKey();
        Assert.Null(ServiceVaultUnlock.Accept(installation, machine, id, key));
        Assert.True(File.Exists(Path.Combine(machine, "keys", id + ".service.bin")));
        var service = ServiceVaultUnlock.ServiceVault(installation, machine);
        Assert.Equal("repo-secret", service.Get("dest:1:storagepw"));
        app.ChangePassword("another");
        Assert.Equal("repo-secret", ServiceVaultUnlock.ServiceVault(installation, machine).Get("dest:1:storagepw"));
    }

    [Fact]
    public void Without_a_vault_the_service_is_set_up_but_a_vault_needs_its_key()
    {
        using var ws = new TempWorkspace();
        var installation = ws.Sub("empty");
        Directory.CreateDirectory(Path.Combine(installation, AppPaths.AppFolderName, "service"));
        Assert.Null(ServiceVaultUnlock.Accept(installation, ws.Sub("m"), null, null));
        var (withVault, _, machine, _) = Install(ws);
        Assert.Equal("vault-key-missing", ServiceVaultUnlock.Accept(withVault, machine, null, null));
    }

    [Theory]
    [InlineData("{\"version\":1,\"command\":\"unlock\",\"vaultId\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"version\":1,\"command\":\"unlock\",\"key\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}")]
    [InlineData("{\"version\":1,\"command\":\"unlock\",\"vaultId\":\"../x\",\"key\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}")]
    [InlineData("{\"version\":1,\"command\":\"unlock\",\"vaultId\":\"0123456789abcdef0123456789abcdef\",\"key\":\"AAAA\"}")]
    [InlineData("{\"version\":1,\"command\":\"activity\",\"key\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}")]
    [InlineData("{\"version\":1,\"command\":\"unlock\",\"backupId\":\"b\"}")]
    // The pre-vault conversion's commands are gone (U153).
    [InlineData("{\"version\":1,\"command\":\"freeze\"}")]
    [InlineData("{\"version\":1,\"command\":\"thaw\"}")]
    public void Malformed_unlock_requests_are_refused(string json)
        => Assert.ThrowsAny<Exception>(() => ServiceHostRequest.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void A_well_formed_unlock_request_parses()
    {
        var request = ServiceHostRequest.Parse(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = 1, command = "unlock", vaultId = "0123456789abcdef0123456789abcdef",
            key = Convert.ToBase64String(new byte[32]),
        })));
        Assert.Equal("unlock", request.Command);
        Assert.Equal("unlock", ServiceHostRequest.Parse(Encoding.UTF8.GetBytes("{\"version\":1,\"command\":\"unlock\",\"vaultId\":null,\"key\":null}")).Command);
    }

    [Fact]
    public void Engine_working_files_live_on_this_computer_named_after_the_configuration()
    {
        using var ws = new TempWorkspace();
        var machine = ws.Sub("machine");
        Environment.SetEnvironmentVariable(EngineWorkPaths.MachineRootVariable, machine);
        try
        {
            var root = EngineWorkPaths.Root;
            Assert.Equal(Path.Combine(machine, "engine", EngineWorkPaths.ConfigHash(ws.ConfigRoot)), root);
            Assert.Matches("^[0-9a-f]{16}$", EngineWorkPaths.ConfigHash(ws.ConfigRoot));
            Assert.Equal(EngineWorkPaths.ConfigHash(ws.ConfigRoot.ToUpperInvariant()), EngineWorkPaths.ConfigHash(ws.ConfigRoot.ToLowerInvariant() + "\\"));
            Assert.NotEqual(EngineWorkPaths.ConfigHash(ws.ConfigRoot), EngineWorkPaths.ConfigHash(Path.Combine(ws.ConfigRoot, "service")));
            Assert.Equal(Path.Combine(root, "backrest"), AppPaths.BackrestDataDir);
            Assert.Equal(Path.Combine(root, "rclone.conf"), RcloneConfigBuilder.DefaultPath);
            Assert.StartsWith(Path.Combine(root, "engine-runs"), BackrestSidecar.CreateIsolatedDataDirectory("scheduled"));
            Assert.Equal(Path.Combine(machine, "keys"), EngineWorkPaths.KeysDirectory);
            foreach (var path in new[] { AppPaths.BackrestDataDir, RcloneConfigBuilder.DefaultPath, EngineWorkPaths.RcloneRemotesDir, EngineWorkPaths.ScratchDir, EngineWorkPaths.KeysDirectory })
                Assert.False(path.StartsWith(ws.ConfigRoot, StringComparison.OrdinalIgnoreCase), path);
        }
        finally { Environment.SetEnvironmentVariable(EngineWorkPaths.MachineRootVariable, null); }
        // A test configuration outside an executable's folder keeps them inside its own engine folder.
        Assert.Equal(Path.Combine(ws.ConfigRoot, "engine"), EngineWorkPaths.Root);
        Assert.False(EngineWorkPaths.IsPortableConfig());
    }
}
