using System;
using System.IO;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceOwnerSecretsTests
{
    private sealed class SyntheticCrypto : ISecretsEncryption
    {
        public string DisplayName => "test";
        public byte[] Protect(byte[] data, byte[] entropy) => (byte[])data.Clone();
        public byte[] Unprotect(byte[] data, byte[] entropy) => (byte[])data.Clone();
    }

    [Fact]
    public void BeforeRegistrationDesktopReadsAndWritesTheOriginalVault()
    {
        using var ws = new TempWorkspace();
        var local = new SecretsStore(new SyntheticCrypto(), Path.Combine(ServicePortableLayout.Config(ws.Root), "secrets.bin"));
        var backend = new ServiceOwnerSecrets(ws.Root, local);
        backend.Set("token", "first"); Assert.Equal("first", local.Get("token"));
        local.Set("token", "newer"); Assert.True(backend.TryGet("token", out var current)); Assert.Equal("newer", current);
        backend.Remove("token"); Assert.False(local.TryGet("token", out _));
    }

    [Fact]
    public void AnUntrustedRegistrationCannotCauseFallbackOrOverwriteTheOriginalVault()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(ServicePortableLayout.Config(ws.Root), "secrets.bin");
        var local = new SecretsStore(new SyntheticCrypto(), path); local.Set("token", "keep");
        Directory.CreateDirectory(ServicePortableLayout.Control(ws.Root));
        File.WriteAllText(ServicePortableLayout.Record(ws.Root, ServiceWorkerBinding.FileName), "invalid and unprotected");
        var before = File.ReadAllBytes(path);
        var backend = new ServiceOwnerSecrets(ws.Root, local);
        Assert.ThrowsAny<Exception>(() => backend.Set("token", "must not be written"));
        Assert.ThrowsAny<Exception>(() => backend.TryGet("token", out _));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void IsolatedConfigurationsNeverAttachToTheApplicationService()
    {
        Assert.True(ServiceOwnerSecrets.Applies(@"D:\Backspace", @"D:\Backspace\Backspace.config"));
        Assert.False(ServiceOwnerSecrets.Applies(@"D:\Backspace", @"D:\Review\Backspace.config"));
        Assert.False(ServiceOwnerSecrets.Applies(@"D:\Backspace", @"D:\Backspace\Backspace.config\windows-service\data"));
    }
}
