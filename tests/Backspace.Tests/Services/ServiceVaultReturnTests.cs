using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceVaultReturnTests
{
    [Fact]
    public void Completed_removal_requires_an_activated_local_binding_and_verified_ciphertext()
    {
        const string root = @"C:\PortableBackup";
        var binding = new ServiceWorkerBinding(2, "Backspace-" + Guid.NewGuid().ToString("N"),
            "S-1-5-21-1-2-3-1001", ServicePortableLayout.Data(root), Activated: true);
        var saved = new ServiceRetirement.Record(1, root, Guid.NewGuid().ToString("N"), binding,
            "S-1-5-80-1-2-3-4-5", "complete", new string('A', 64));
        ServiceRetirement.Validate(saved);
        ServiceRetirement.Validate(saved with { Stage = "removing" });
        Assert.Throws<System.Security.SecurityException>(() => ServiceRetirement.Validate(saved with { OwnerCiphertextSha256 = null }));
        Assert.Throws<System.Security.SecurityException>(() => ServiceRetirement.Validate(saved with { Binding = binding with { Activated = false } }));
        Assert.Throws<System.Security.SecurityException>(() => ServiceRetirement.Validate(saved with { Binding = binding with { DataDirectory = @"C:\OtherData" } }));
        Assert.Throws<System.Security.SecurityException>(() => ServiceRetirement.Validate(saved with { Stage = "unknown" }));
    }

    private sealed class Crypto(byte marker) : ISecretsEncryption
    {
        public string DisplayName => "Synthetic return identity";
        public byte[] Protect(byte[] clear, byte[] entropy) => [marker, ..clear];
        public byte[] Unprotect(byte[] wrapped, byte[] entropy) => wrapped[0] == marker ? wrapped[1..] : throw new CryptographicException();
    }

    [Fact]
    public void Latest_service_tokens_are_reencrypted_verified_and_held_until_removal()
    {
        using var ws = new TempWorkspace();
        var ownerPath = Path.Combine(AppPaths.ConfigRoot, "owner.bin"); var servicePath = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        var owner = new SecretsStore(new Crypto(11), ownerPath); owner.Set("token", "old");
        var original = File.ReadAllBytes(ownerPath);
        var service = new SecretsStore(new Crypto(22), servicePath); service.Set("token", "latest"); service.Set("new", "service-only");
        var serviceBefore = File.ReadAllBytes(servicePath);
        var id = Guid.NewGuid().ToString("N"); var receipt = Path.Combine(AppPaths.ConfigRoot, "return.accepted");
        var protocol = new ServiceVaultReturnProtocol(id, receipt, service);
        var removed = false;
        ServiceVaultReturn.Transfer(owner, ownerPath, id, request => JsonSerializer.Deserialize<ServiceVaultReturnProtocol.Response>(protocol.Dispatch(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json)), ServiceRunAuthorization.Json)!, () =>
        {
            Assert.Equal("latest", new SecretsStore(new Crypto(11), ownerPath).Get("token"));
            Assert.Equal("service-only", owner.Get("new"));
            Assert.Throws<IOException>(() => File.Open(ownerPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            Assert.True(File.Exists(receipt)); removed = true;
        });
        Assert.True(removed); Assert.Equal(serviceBefore, File.ReadAllBytes(servicePath)); Assert.Equal(original, File.ReadAllBytes(ownerPath + ".bak"));
        Assert.DoesNotContain("latest", File.ReadAllText(receipt)); Assert.DoesNotContain("service-only", File.ReadAllText(receipt));
        var returnedBytes = File.ReadAllBytes(ownerPath);
        ServiceVaultReturn.Transfer(owner, ownerPath, id, request => JsonSerializer.Deserialize<ServiceVaultReturnProtocol.Response>(protocol.Dispatch(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json)), ServiceRunAuthorization.Json)!, () => { });
        Assert.Equal(returnedBytes, File.ReadAllBytes(ownerPath));
        Assert.Equal(original, File.ReadAllBytes(ownerPath + ".bak"));
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("wrong-digest")]
    [InlineData("extra-path")]
    [InlineData("duplicate-command")]
    public void Invalid_acceptance_never_creates_a_receipt(string kind)
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(AppPaths.ConfigRoot, "service.bin"); var service = new SecretsStore(new Crypto(22), path); service.Set("token", "latest");
        var id = Guid.NewGuid().ToString("N"); var receipt = path + ".accepted"; var protocol = new ServiceVaultReturnProtocol(id, receipt, service);
        var request = JsonSerializer.Serialize(new ServiceVaultReturnProtocol.Request(1, "accept", kind == "wrong-id" ? Guid.NewGuid().ToString("N") : id,
            new string('0', 64), 1, new string('1', 64)), ServiceRunAuthorization.Json);
        if (kind == "extra-path") request = request[..^1] + ",\"path\":\"C:/Windows\"}";
        if (kind == "duplicate-command") request = request[..^1] + ",\"command\":\"export\"}";
        var response = JsonSerializer.Deserialize<ServiceVaultReturnProtocol.Response>(protocol.Dispatch(Encoding.UTF8.GetBytes(request)), ServiceRunAuthorization.Json)!;
        Assert.NotNull(response.Error); Assert.False(File.Exists(receipt)); Assert.Equal("latest", service.Get("token"));
    }

    [Fact]
    public void Missing_service_vault_and_failed_acceptance_cannot_authorize_removal()
    {
        using var ws = new TempWorkspace(); var path = Path.Combine(AppPaths.ConfigRoot, "owner.bin");
        var owner = new SecretsStore(new Crypto(11), path); owner.Set("token", "old");
        var id = Guid.NewGuid().ToString("N"); var removed = false;
        var missing = new ServiceVaultReturnProtocol(id, path + ".accepted", new SecretsStore(new Crypto(22), path + ".missing"));
        Assert.Throws<IOException>(() => ServiceVaultReturn.Transfer(owner, path, id, request => JsonSerializer.Deserialize<ServiceVaultReturnProtocol.Response>(missing.Dispatch(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json)), ServiceRunAuthorization.Json)!, () => removed = true));
        Assert.False(removed); Assert.Equal("old", owner.Get("token")); Assert.False(File.Exists(path + ".missing"));
        Assert.Throws<IOException>(() => ServiceVaultReturn.Transfer(owner, path, id,
            request => request.Command == "export" ? new(1, new() { ["token"] = "latest" }) : new(1, Error: "lost-response"), () => removed = true));
        Assert.False(removed); Assert.Equal("latest", owner.Get("token")); Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Frozen_service_credentials_can_be_read_but_not_changed()
    {
        using var ws = new TempWorkspace(); var path = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        var vault = new SecretsStore(new Crypto(22), path); vault.Set("token", "latest"); var before = File.ReadAllBytes(path);
        foreach (var command in new[] { "set", "remove" })
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ServiceSecretsProtocol.Request { Command = command, Key = "token", Value = command == "set" ? "older" : null }, ServiceSecretsProtocol.Json);
            var reply = JsonSerializer.Deserialize<ServiceSecretsProtocol.Response>(ServiceSecretsProtocol.Dispatch(bytes, vault, () => true), ServiceSecretsProtocol.Json)!;
            Assert.NotNull(reply.Error);
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        var get = JsonSerializer.SerializeToUtf8Bytes(new ServiceSecretsProtocol.Request { Command = "get", Key = "token" }, ServiceSecretsProtocol.Json);
        Assert.Equal("latest", JsonSerializer.Deserialize<ServiceSecretsProtocol.Response>(ServiceSecretsProtocol.Dispatch(get, vault, () => true), ServiceSecretsProtocol.Json)!.Value);
    }
}
