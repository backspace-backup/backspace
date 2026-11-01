using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class SecretsStartupWarningTests
{
    [Fact]
    public async Task Unavailable_decryption_context_can_recover_without_replacing_any_saved_secret()
    {
        using var workspace = new TempWorkspace();
        var crypto = new SessionEncryption();
        var store = new SecretsStore(crypto);
        store.Set("test-ref", "original-value");
        var original = File.ReadAllBytes(AppPaths.SecretsFile);

        crypto.CanDecrypt = false;
        var warning = await SecretsStartupWarning.CheckAsync(store);
        Assert.NotNull(warning);
        if (PlatformInfo.IsWindows)
        {
            Assert.Contains("First, sign in", warning.Message);
            Assert.Contains("passwords do not need replacing", warning.Message);
        }
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SecretsFile));
        Assert.Empty(Directory.GetFiles(workspace.ConfigRoot, "secrets.bin.unreadable-*"));

        // Models becoming able to decrypt in a different logon context. This
        // does not claim to execute a real Windows sign-in or scheduled task.
        crypto.CanDecrypt = true;
        Assert.Null(await SecretsStartupWarning.CheckAsync(store));
        Assert.Equal("original-value", store.Get("test-ref"));
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SecretsFile));
    }

    private sealed class SessionEncryption : ISecretsEncryption
    {
        public bool CanDecrypt { get; set; } = true;
        public string DisplayName => "Synthetic session test";
        public byte[] Protect(byte[] cleartext, byte[] entropy) => cleartext;
        public byte[] Unprotect(byte[] ciphertext, byte[] entropy) => CanDecrypt
            ? ciphertext : throw new CryptographicException("Synthetic session cannot decrypt");
    }

    [Fact]
    public async Task Startup_opens_lazy_vault_preserves_corrupt_bytes_and_warns_without_any_prior_read()
    {
        using var workspace = new TempWorkspace();
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllBytes(AppPaths.SecretsFile, bytes);
        var store = new SecretsStore();
        Assert.Null(store.LoadErrorMessage);
        var warning = await SecretsStartupWarning.CheckAsync(store);
        Assert.NotNull(warning);
        Assert.Equal("Saved passwords couldn't be opened", warning.Title);
        Assert.Contains("before reconnecting destinations", warning.Message);
        Assert.NotNull(store.PreservedCorruptPath);
        Assert.Equal(bytes, File.ReadAllBytes(store.PreservedCorruptPath));
        Assert.Equal(bytes, File.ReadAllBytes(AppPaths.SecretsFile));
        Assert.Empty(Directory.GetFiles(workspace.ConfigRoot, "secrets.bin.unreadable-*"));
        var nextLaunch = await SecretsStartupWarning.CheckAsync(new SecretsStore());
        Assert.NotNull(nextLaunch);
        Assert.Equal(warning.Title, nextLaunch.Title);
        Assert.Equal(bytes, File.ReadAllBytes(AppPaths.SecretsFile));
    }

    [Fact]
    public async Task Startup_temporary_lock_warns_without_replacing_secrets_and_retries_successfully()
    {
        using var workspace = new TempWorkspace();
        var initial = new SecretsStore();
        initial.Set("test-ref", "keep-original-value");
        var original = File.ReadAllBytes(AppPaths.SecretsFile);
        var store = new SecretsStore();
        using (var locked = new FileStream(AppPaths.SecretsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.ValidateReadable());
            var warning = await SecretsStartupWarning.CheckAsync(store);
            Assert.NotNull(warning);
            Assert.Contains("temporarily unavailable", warning.Title);
            Assert.Contains("have not been changed", warning.Message);
            Assert.DoesNotContain("Re-enter", warning.Message);
            Assert.Null(store.LoadErrorMessage);
            Assert.Null(store.PreservedCorruptPath);
            Assert.Empty(Directory.GetFiles(workspace.ConfigRoot, "secrets.bin.unreadable-*"));
        }
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SecretsFile));
        Assert.Null(await SecretsStartupWarning.CheckAsync(store));
        Assert.Equal("keep-original-value", store.Get("test-ref"));
    }

    [Fact]
    public async Task Startup_valid_or_absent_vault_has_no_warning_or_credential_file_mutation()
    {
        using var workspace = new TempWorkspace();
        Assert.Null(await SecretsStartupWarning.CheckAsync(new SecretsStore()));
        Assert.False(File.Exists(AppPaths.SecretsFile));
        var store = new SecretsStore();
        store.Set("test-ref", "unchanged");
        var original = File.ReadAllBytes(AppPaths.SecretsFile);
        Assert.Null(await SecretsStartupWarning.CheckAsync(new SecretsStore()));
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SecretsFile));
        Assert.DoesNotContain(Directory.EnumerateFiles(workspace.ConfigRoot), f => f.Contains("unreadable-", StringComparison.Ordinal));
    }
}
