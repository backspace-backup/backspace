using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class SecretsMigrationTests
{
    // Deterministic isolated identity providers. Native cross-account DPAPI
    // acceptance belongs in the VM, never an experiment on host credentials.
    private sealed class IdentityCrypto(byte marker) : ISecretsEncryption
    {
        public string DisplayName => "Synthetic identity";
        public byte[] Protect(byte[] value, byte[] entropy)
        {
            var result = new byte[value.Length + 1]; result[0] = marker;
            for (var n = 0; n < value.Length; n++) result[n + 1] = (byte)(value[n] ^ marker);
            return result;
        }
        public byte[] Unprotect(byte[] value, byte[] entropy)
        {
            if (value.Length == 0 || value[0] != marker) throw new CryptographicException("Wrong synthetic identity");
            var result = new byte[value.Length - 1];
            for (var n = 0; n < result.Length; n++) result[n] = (byte)(value[n + 1] ^ marker);
            return result;
        }
    }

    [Fact]
    public void ImportReencryptsWithoutChangingTheOriginalAndReopensIndependently()
    {
        using var ws = new TempWorkspace();
        var source = Path.Combine(AppPaths.ConfigRoot, "desktop.bin");
        var target = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        var desktop = new SecretsStore(new IdentityCrypto(11), source);
        desktop.Set("repo:one", "synthetic password");
        desktop.Set("oauth:one", "synthetic token");
        var original = File.ReadAllBytes(source);
        var snapshot = desktop.ExportForMigration();
        new SecretsStore(new IdentityCrypto(22), target).ImportIntoNewVault(snapshot);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.NotEqual(original, File.ReadAllBytes(target));
        var service = new SecretsStore(new IdentityCrypto(22), target);
        Assert.Equal(snapshot, service.ExportForMigration());
        service.Set("oauth:one", "refreshed token");
        Assert.Equal("refreshed token", new SecretsStore(new IdentityCrypto(22), target).Get("oauth:one"));
        Assert.Equal("synthetic token", desktop.Get("oauth:one"));
        Assert.Equal("synthetic token", snapshot["oauth:one"]);
    }

    [Fact]
    public void UnreadableSourceIsNotExportedAsAnEmptyMap()
    {
        using var ws = new TempWorkspace();
        var source = Path.Combine(AppPaths.ConfigRoot, "desktop.bin");
        new SecretsStore(new IdentityCrypto(11), source).Set("repo", "preserve me");
        var original = File.ReadAllBytes(source);
        Assert.Throws<CryptographicException>(() => new SecretsStore(new IdentityCrypto(22), source).ExportForMigration());
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Single(Directory.GetFiles(AppPaths.ConfigRoot, "desktop.bin*", SearchOption.TopDirectoryOnly), p => !p.EndsWith(".lock"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingTargetIsNeverReplacedEvenWhenUnreadable(bool readable)
    {
        using var ws = new TempWorkspace();
        var target = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        if (readable) new SecretsStore(new IdentityCrypto(22), target).Set("oauth", "newer token");
        else File.WriteAllBytes(target, new byte[] { 1, 2, 3 });
        var original = File.ReadAllBytes(target);
        Assert.Throws<IOException>(() => new SecretsStore(new IdentityCrypto(22), target)
            .ImportIntoNewVault(new Dictionary<string, string> { ["oauth"] = "stale token" }));
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    private sealed class BrokenCrypto : ISecretsEncryption
    {
        public string DisplayName => "Broken synthetic encryption";
        public byte[] Protect(byte[] value, byte[] entropy) => Encoding.UTF8.GetBytes("wrong bytes");
        public byte[] Unprotect(byte[] value, byte[] entropy) => value;
    }

    [Fact]
    public void FailedEncryptionRoundTripLeavesNoTargetVault()
    {
        using var ws = new TempWorkspace();
        var target = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        Assert.Throws<CryptographicException>(() => new SecretsStore(new BrokenCrypto(), target)
            .ImportIntoNewVault(new Dictionary<string, string> { ["repo"] = "do not lose" }));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void SourceRemainsLockedThroughTransferAndActivation()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(AppPaths.ConfigRoot, "desktop.bin");
        var source = new SecretsStore(new IdentityCrypto(11), path);
        source.Set("oauth", "current token");
        var activated = source.WithMigrationSnapshot(snapshot =>
        {
            Assert.Equal("current token", snapshot["oauth"]);
            // Task.Run can inline when synchronously waited, making a
            // thread-local reentrant lease look like an independent writer.
            Exception? failure = null;
            var sourceThread = Environment.CurrentManagedThreadId;
            var competingThread = sourceThread;
            var competitor = new Thread(() =>
            {
                competingThread = Environment.CurrentManagedThreadId;
                try { using var lease = StateFileLock.Acquire(path, TimeSpan.FromMilliseconds(100)); }
                catch (Exception error) { failure = error; }
            });
            competitor.Start();
            Assert.True(competitor.Join(TimeSpan.FromSeconds(3)));
            Assert.NotEqual(sourceThread, competingThread);
            Assert.IsType<IOException>(failure);
            return true;
        });
        Assert.True(activated);
        using var released = StateFileLock.Acquire(path, TimeSpan.FromMilliseconds(100));
        Assert.Equal("current token", source.Get("oauth"));
    }

    private sealed class RacingCrypto(string target) : ISecretsEncryption
    {
        private readonly IdentityCrypto _inner = new(22);
        public string DisplayName => "Racing synthetic writer";
        public byte[] Protect(byte[] value, byte[] entropy)
        {
            File.WriteAllBytes(target, new byte[] { 9, 8, 7 });
            return _inner.Protect(value, entropy);
        }
        public byte[] Unprotect(byte[] value, byte[] entropy) => _inner.Unprotect(value, entropy);
    }

    [Fact]
    public void ATargetCreatedDuringEncryptionIsPreserved()
    {
        using var ws = new TempWorkspace();
        var target = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        Assert.Throws<IOException>(() => new SecretsStore(new RacingCrypto(target), target)
            .ImportIntoNewVault(new Dictionary<string, string> { ["oauth"] = "older token" }));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(target));
        Assert.Empty(Directory.GetFiles(AppPaths.ConfigRoot, "*.migration-*.tmp"));
    }
}
