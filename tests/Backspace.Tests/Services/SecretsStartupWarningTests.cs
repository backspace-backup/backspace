using System;
using System.IO;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>Start-up checks of the saved passwords (U153): a damaged vault
/// warns and is kept as it is; a locked one does not warn (the Backups page
/// shows a banner instead); a file another program holds is waited out.</summary>
public sealed class SecretsStartupWarningTests
{
    [Fact]
    public async Task A_damaged_vault_warns_and_its_bytes_are_kept_across_launches()
    {
        using var workspace = new TempWorkspace(vault: false);
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllBytes(AppPaths.VaultFile, bytes);
        var store = new SecretsStore();
        Assert.Null(store.LoadErrorMessage);
        var warning = await SecretsStartupWarning.CheckAsync(store);
        Assert.NotNull(warning);
        Assert.Equal("Saved passwords couldn't be opened", warning.Title);
        Assert.Equal(bytes, File.ReadAllBytes(AppPaths.VaultFile));
        var nextLaunch = await SecretsStartupWarning.CheckAsync(new SecretsStore());
        Assert.NotNull(nextLaunch);
        Assert.Equal(bytes, File.ReadAllBytes(AppPaths.VaultFile));
        Assert.Empty(Directory.GetFiles(workspace.ConfigRoot, "vault.bin.unreadable-*"));
    }

    [Fact]
    public async Task A_locked_vault_does_not_warn_at_start()
    {
        using var workspace = new TempWorkspace(vault: false);
        new SecretsStore().Create("pw");
        Directory.Delete(EngineWorkPaths.KeysDirectory, recursive: true);
        var store = new SecretsStore();
        Assert.Null(await SecretsStartupWarning.CheckAsync(store));
        Assert.Equal(VaultState.Locked, store.State);
    }

    [Fact]
    public async Task A_file_held_by_another_program_is_waited_out_without_a_warning()
    {
        PlatformTest.WindowsOnly("a file another program holds open (Linux and macOS have no such sharing locks)");
        using var workspace = new TempWorkspace();
        var initial = new SecretsStore();
        initial.Set("test-ref", "keep-original-value");
        var original = File.ReadAllBytes(AppPaths.VaultFile);
        var store = new SecretsStore();
        var locked = new FileStream(AppPaths.VaultFile, FileMode.Open, FileAccess.Read, FileShare.None);
        var check = SecretsStartupWarning.CheckAsync(store);
        await Task.Delay(1500);
        Assert.False(check.IsCompleted);
        locked.Dispose();
        Assert.Null(await check);
        Assert.Equal(original, File.ReadAllBytes(AppPaths.VaultFile));
        Assert.Equal("keep-original-value", store.Get("test-ref"));
    }

    [Fact]
    public async Task A_valid_or_absent_vault_has_no_warning_and_is_not_written()
    {
        using var workspace = new TempWorkspace(vault: false);
        Assert.Null(await SecretsStartupWarning.CheckAsync(new SecretsStore()));
        Assert.False(File.Exists(AppPaths.VaultFile));
        var store = new SecretsStore();
        store.Create("");
        store.Set("test-ref", "unchanged");
        var original = File.ReadAllBytes(AppPaths.VaultFile);
        Assert.Null(await SecretsStartupWarning.CheckAsync(new SecretsStore()));
        Assert.Equal(original, File.ReadAllBytes(AppPaths.VaultFile));
    }
}
