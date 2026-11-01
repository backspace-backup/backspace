using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceSecretsTests
{
    private sealed class SyntheticCrypto : ISecretsEncryption
    {
        public string DisplayName => "Synthetic test provider";
        public byte[] Protect(byte[] value, byte[] entropy) => (byte[])value.Clone();
        public byte[] Unprotect(byte[] value, byte[] entropy) => (byte[])value.Clone();
    }
    private static ServiceSecretsProtocol.Response Send(SecretsStore store, string json) =>
        JsonSerializer.Deserialize<ServiceSecretsProtocol.Response>(ServiceSecretsProtocol.Dispatch(Encoding.UTF8.GetBytes(json), store), ServiceSecretsProtocol.Json)!;

    [Fact]
    public void DispatcherReadsAndRotatesOneAuthorityWithoutReplacingOtherValues()
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        var service = new SecretsStore(new SyntheticCrypto(), path);
        service.ImportIntoNewVault(new Dictionary<string,string> { ["oauth"] = "first", ["repo"] = "unchanged" });
        Assert.Null(Send(service, "{\"command\":\"validate\"}").Error);
        Assert.Equal("first", Send(service, "{\"command\":\"get\",\"key\":\"oauth\"}").Value);
        Assert.Null(Send(service, "{\"command\":\"set\",\"key\":\"oauth\",\"value\":\"refreshed\"}").Error);
        var reopened = new SecretsStore(new SyntheticCrypto(), path);
        Assert.Equal("refreshed", Send(reopened, "{\"command\":\"get\",\"key\":\"oauth\"}").Value);
        Assert.Equal("unchanged", reopened.Get("repo"));
        Assert.Null(Send(reopened, "{\"command\":\"remove\",\"key\":\"oauth\"}").Error);
        var missing = Send(service, "{\"command\":\"get\",\"key\":\"oauth\"}");
        Assert.False(missing.Found);
        Assert.Null(missing.Value);
        Assert.Equal("unchanged", service.Get("repo"));
    }

    [Theory]
    [InlineData("{\"command\":\"export\"}")]
    [InlineData("{\"command\":\"set\",\"key\":\"oauth\"}")]
    [InlineData("{\"version\":2,\"command\":\"remove\",\"key\":\"oauth\"}")]
    [InlineData("{\"command\":\"get\",\"key\":\"oauth\",\"path\":\"C:\\\\secrets.bin\"}")]
    [InlineData("{\"command\":\"validate\",\"key\":\"oauth\"}")]
    [InlineData("{\"command\":\"get\",\"key\":\"oauth\",\"value\":\"leak\"}")]
    [InlineData("{\"command\":\"get\",\"command\":\"remove\",\"key\":\"oauth\"}")]
    [InlineData("{\"command\":\"remove\",\"key\":null}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    public void InvalidRequestsDoNotChangeTheVaultOrEchoPayload(string request)
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        var service = new SecretsStore(new SyntheticCrypto(), path);
        service.Set("oauth", "preserve this synthetic secret");
        var before = File.ReadAllBytes(path);
        var reply = Send(service, request);
        Assert.Equal("invalid-request", reply.Error);
        Assert.Null(reply.Value);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrUnreadableVaultCannotBecomeAnEmptySuccessfulReadOrReplacement(bool exists)
    {
        using var ws = new TempWorkspace();
        var path = Path.Combine(AppPaths.ConfigRoot, "service.bin");
        var original = Encoding.UTF8.GetBytes("not valid encrypted JSON");
        if (exists) File.WriteAllBytes(path, original);
        var service = new SecretsStore(new SyntheticCrypto(), path);
        foreach (var request in new[] { "{\"command\":\"validate\"}", "{\"command\":\"get\",\"key\":\"oauth\"}",
                     "{\"command\":\"set\",\"key\":\"oauth\",\"value\":\"stale token\"}", "{\"command\":\"remove\",\"key\":\"oauth\"}" })
            Assert.Equal("vault-unavailable", Send(service, request).Error);
        Assert.Equal(exists, File.Exists(path));
        if (exists) Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(AppPaths.ConfigRoot, "*.unreadable-*"));
    }

    private sealed class RecordingBackend : ISecretsBackend
    {
        internal Dictionary<string, string> Values { get; } = new();
        internal bool Fail;
        public void ValidateReadable() { if (Fail) throw new IOException("Service unavailable"); }
        public bool TryGet(string key, out string value) { ValidateReadable(); return Values.TryGetValue(key, out value!); }
        public void Set(string key, string value) { ValidateReadable(); Values[key] = value; }
        public void Remove(string key) { ValidateReadable(); Values.Remove(key); }
    }

    [Fact]
    public void ExistingSecretsConsumersUseRemoteAuthorityAndNeverFallBackOnFailure()
    {
        using var ws = new TempWorkspace();
        var old = new SecretsStore(new SyntheticCrypto());
        old.Set("oauth", "stale local token");
        var original = File.ReadAllBytes(AppPaths.SecretsFile);
        var backend = new RecordingBackend();
        backend.Values["oauth"] = "service token";
        var desktop = new SecretsStore(backend);
        var otherDesktop = new SecretsStore(backend);
        Assert.Equal("service token", desktop.Get("oauth"));
        desktop.Set("oauth", "refreshed token");
        Assert.Equal("refreshed token", otherDesktop.Get("oauth"));
        var key = desktop.PutNew("repo", "new synthetic password");
        Assert.True(otherDesktop.TryGet(key, out var value));
        Assert.Equal("new synthetic password", value);
        desktop.Remove(key);
        Assert.False(otherDesktop.TryGet(key, out _));
        backend.Fail = true;
        Assert.Throws<IOException>(() => desktop.Get("oauth"));
        Assert.Throws<IOException>(() => desktop.Set("oauth", "must not be saved"));
        Assert.Throws<IOException>(() => desktop.Remove("oauth"));
        Assert.Throws<IOException>(() => desktop.ValidateReadable());
        Assert.Throws<InvalidOperationException>(() => desktop.ExportForMigration());
        Assert.Throws<InvalidOperationException>(() => desktop.ImportIntoNewVault(new Dictionary<string,string>()));
        Assert.Equal(original, File.ReadAllBytes(AppPaths.SecretsFile));
    }

    [Fact]
    public void AsyncMigrationCallbacksAreRejectedBeforeExecuting()
    {
        using var ws = new TempWorkspace();
        var source = new SecretsStore(new SyntheticCrypto());
        source.Set("oauth", "preserve");
        var invoked = false;
        Assert.Throws<ArgumentException>(() => { _ = source.WithMigrationSnapshot(_ => { invoked = true; return Task.CompletedTask; }); });
        Assert.Throws<ArgumentException>(() => { _ = source.WithMigrationSnapshot(_ => { invoked = true; return ValueTask.CompletedTask; }); });
        Assert.Throws<ArgumentException>(() => { _ = source.WithMigrationSnapshot(_ => { invoked = true; return ValueTask.FromResult(true); }); });
        Assert.False(invoked);
    }

    [Fact]
    public void OversizedValuesAreRejectedBeforeTouchingVault()
    {
        using var ws = new TempWorkspace();
        var store = new SecretsStore(new SyntheticCrypto());
        var request = JsonSerializer.Serialize(new ServiceSecretsProtocol.Request { Command = "set", Key = "oauth", Value = new string('x', 128 * 1024 + 1) }, ServiceSecretsProtocol.Json);
        Assert.Equal("invalid-request", Send(store, request).Error);
        Assert.False(File.Exists(AppPaths.SecretsFile));
    }
}
