using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Services.Platform;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceMigrationProtocolTests
{
    private sealed class SyntheticCrypto : ISecretsEncryption
    {
        public string DisplayName => "Synthetic migration fixture";
        public byte[] Protect(byte[] value, byte[] entropy) => (byte[])value.Clone();
        public byte[] Unprotect(byte[] value, byte[] entropy) => (byte[])value.Clone();
    }
    private const string Migration = "0123456789abcdef0123456789abcdef";
    private sealed class RefreshLease(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    [Fact]
    public void Cancelled_setup_can_refresh_only_the_exact_inactive_copy_and_requires_fresh_acceptance()
    {
        using var ws = new TempWorkspace();
        var receipt = Path.Combine(ws.Root, "receipt");
        var vault = new SecretsStore(new SyntheticCrypto(), Path.Combine(ws.Root, "target.bin"));
        var active = false; var held = false;
        var protocol = new ServiceMigrationProtocol(new(1, "owner", Migration), receipt, vault, () =>
        {
            if (active) throw new IOException("Already active");
            held = true; return new RefreshLease(() => held = false);
        });
        var first = new Dictionary<string, string> { ["oauth"] = "old desktop token" };
        Assert.Null(Send(protocol, new(1, "import", Migration, first)).Error);
        var verified = Send(protocol, new(1, "verify", Migration));
        Assert.Null(Send(protocol, new(1, "accept", Migration, ExpectedDigest: verified.Digest, ExpectedCount: verified.Count)).Error);
        Assert.True(File.Exists(receipt + ".accepted"));
        var newer = new Dictionary<string, string> { ["oauth"] = "new desktop token", ["repo"] = "new password" };
        Assert.Null(Send(protocol, new(1, "refresh", Migration, newer, verified.Digest, verified.Count)).Error);
        Assert.False(held); Assert.False(File.Exists(receipt + ".accepted"));
        Assert.Equal("new desktop token", vault.Get("oauth"));
        Assert.Equal("new password", vault.Get("repo"));
        Assert.NotNull(Send(protocol, new(1, "refresh", Migration, first, verified.Digest, verified.Count)).Error);
        Assert.Equal("new desktop token", vault.Get("oauth"));
        var current = Send(protocol, new(1, "verify", Migration));
        Assert.Null(Send(protocol, new(1, "accept", Migration, ExpectedDigest: current.Digest, ExpectedCount: current.Count)).Error);
        active = true;
        Assert.NotNull(Send(protocol, new(1, "refresh", Migration, first, current.Digest, current.Count)).Error);
        Assert.True(File.Exists(receipt + ".accepted")); Assert.Equal("new desktop token", vault.Get("oauth"));
    }

    [Fact]
    public void Inactive_refresh_never_recreates_a_missing_target()
    {
        using var ws = new TempWorkspace(); var path = Path.Combine(ws.Root, "target.bin"); var receipt = Path.Combine(ws.Root, "receipt");
        File.WriteAllText(receipt, "consumed");
        var protocol = new ServiceMigrationProtocol(new(1, "owner", Migration), receipt, new(new SyntheticCrypto(), path), () => new RefreshLease(() => { }));
        var secrets = new Dictionary<string, string>();
        Assert.NotNull(Send(protocol, new(1, "refresh", Migration, secrets, ServiceMigrationProtocol.Digest(secrets), 0)).Error);
        Assert.False(File.Exists(path));
    }

    private static ServiceMigrationProtocol.Response Send(ServiceMigrationProtocol target, ServiceMigrationProtocol.Request request) =>
        JsonSerializer.Deserialize<ServiceMigrationProtocol.Response>(target.Dispatch(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json)), ServiceRunAuthorization.Json)!;

    [Fact]
    public async Task Rejected_import_releases_the_protocol_lock_for_another_thread()
    {
        using var ws = new TempWorkspace();
        var vault = new SecretsStore(new SyntheticCrypto(), Path.Combine(ws.Root, "target.bin"));
        var protocol = new ServiceMigrationProtocol(new(1, "owner", Migration), Path.Combine(ws.Root, "receipt"), vault);
        var original = new Dictionary<string, string> { ["oauth"] = "original token" };
        ServiceMigrationProtocol.Response OnSeparateThread(ServiceMigrationProtocol.Request request)
        {
            ServiceMigrationProtocol.Response? response = null;
            Exception? failure = null;
            var thread = new Thread(() => { try { response = Send(protocol, request); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(3)), "A previous request retained the protocol lock.");
            Assert.Null(failure);
            return response!;
        }
        Assert.Null(OnSeparateThread(new(1, "import", Migration, original)).Error);
        Assert.Equal("migration-needs-recovery", OnSeparateThread(new(1, "import", Migration, original)).Error);
        var verified = OnSeparateThread(new(1, "verify", Migration));
        Assert.Null(verified.Error);
        Assert.Equal(ServiceMigrationProtocol.Digest(original), verified.Digest);
        await Task.CompletedTask;
    }

    [Fact]
    public void Activation_receipt_requires_the_owners_verified_persisted_copy()
    {
        using var ws = new TempWorkspace();
        var receipt = Path.Combine(ws.Root, "receipt");
        var vault = new SecretsStore(new SyntheticCrypto(), Path.Combine(ws.Root, "target.bin"));
        var protocol = new ServiceMigrationProtocol(new(1, "owner", Migration), receipt, vault);
        var original = new Dictionary<string, string> { ["oauth"] = "first token" };
        var expected = ServiceMigrationProtocol.Digest(original);
        Assert.NotNull(Send(protocol, new(1, "accept", Migration, ExpectedDigest: expected, ExpectedCount: 1)).Error);
        Assert.False(File.Exists(receipt + ".accepted"));
        Assert.Null(Send(protocol, new(1, "import", Migration, original)).Error);
        Assert.Null(Send(protocol, new(1, "verify", Migration)).Error);
        Assert.False(File.Exists(receipt + ".accepted"));
        vault.Set("oauth", "newer token");
        Assert.NotNull(Send(protocol, new(1, "accept", Migration, ExpectedDigest: expected, ExpectedCount: 1)).Error);
        Assert.False(File.Exists(receipt + ".accepted"));
        var current = Send(protocol, new(1, "verify", Migration));
        Assert.Null(Send(protocol, new(1, "accept", Migration, ExpectedDigest: current.Digest, ExpectedCount: current.Count)).Error);
        var saved = File.ReadAllText(receipt + ".accepted");
        Assert.DoesNotContain("token", saved); Assert.DoesNotContain(current.Digest!, saved);
        Assert.Equal(new ServiceMigrationProtocol.Acceptance(1, Migration),
            JsonSerializer.Deserialize<ServiceMigrationProtocol.Acceptance>(saved, ServiceRunAuthorization.Json));
        Assert.Null(Send(protocol, new(1, "accept", Migration, ExpectedDigest: current.Digest, ExpectedCount: current.Count)).Error);
        Assert.Equal("newer token", vault.Get("oauth"));
    }

    [Fact]
    public void Import_is_one_time_verification_rereads_disk_and_never_exports_values()
    {
        using var ws = new TempWorkspace();
        var vault = new SecretsStore(new SyntheticCrypto(), Path.Combine(ws.Root, "target.bin"));
        var protocol = new ServiceMigrationProtocol(new(1,"owner",Migration),Path.Combine(ws.Root,"receipt"),vault);
        var original = new Dictionary<string,string> { ["oauth"]="first token",["repo"]="repository password" };
        Assert.Null(Send(protocol,new(1,"import",Migration,original)).Error);
        var response = Send(protocol,new(1,"verify",Migration));
        Assert.Equal(2,response.Count);Assert.Equal(ServiceMigrationProtocol.Digest(original),response.Digest);
        Assert.DoesNotContain("first token",JsonSerializer.Serialize(response));
        vault.Set("oauth","newer token");
        Assert.Equal("migration-needs-recovery",Send(protocol,new(1,"import",Migration,original)).Error);
        Assert.Equal("newer token",vault.Get("oauth"));
        Assert.NotEqual(response.Digest,Send(protocol,new(1,"verify",Migration)).Digest);
    }

    [Fact]
    public void A_consumed_grant_cannot_recreate_a_missing_vault()
    {
        using var ws = new TempWorkspace();
        var path=Path.Combine(ws.Root,"target.bin");var receipt=Path.Combine(ws.Root,"receipt");
        File.WriteAllText(receipt,"already attempted");
        var protocol=new ServiceMigrationProtocol(new(1,"owner",Migration),receipt,new(new SyntheticCrypto(),path));
        Assert.NotNull(Send(protocol,new(1,"import",Migration,new(){["secret"]="replacement"})).Error);
        Assert.NotNull(Send(protocol,new(1,"verify",Migration)).Error);
        Assert.False(File.Exists(path));Assert.Equal("already attempted",File.ReadAllText(receipt));
    }

    [Fact]
    public void A_retained_vault_backup_is_not_treated_as_an_empty_target()
    {
        using var ws=new TempWorkspace();var path=Path.Combine(ws.Root,"target.bin");
        File.WriteAllText(path+".bak","previous encrypted data");
        var vault=new SecretsStore(new SyntheticCrypto(),path);
        Assert.Throws<IOException>(()=>vault.ImportIntoNewVault(new Dictionary<string,string>{{"secret","replacement"}}));
        Assert.False(File.Exists(path));Assert.Equal("previous encrypted data",File.ReadAllText(path+".bak"));
    }

    [Theory]
    [InlineData("{\"version\":1,\"command\":\"import\",\"migrationId\":\"wrong\",\"secrets\":{}}")]
    [InlineData("{\"version\":1,\"command\":\"export\",\"migrationId\":\"0123456789abcdef0123456789abcdef\"}")]
    [InlineData("{\"version\":1,\"command\":\"import\",\"migrationId\":\"0123456789abcdef0123456789abcdef\",\"secrets\":{\"x\":\"one\",\"x\":\"two\"}}")]
    [InlineData("{\"version\":1,\"command\":\"verify\",\"migrationId\":\"0123456789abcdef0123456789abcdef\",\"secrets\":{}}")]
    [InlineData("{\"version\":1,\"command\":\"import\",\"migrationId\":\"0123456789abcdef0123456789abcdef\",\"secrets\":{\"x\":null}}")]
    [InlineData("{\"version\":1,\"command\":\"import\",\"migrationId\":\"0123456789abcdef0123456789abcdef\",\"path\":\"C:/other\",\"secrets\":{}}")]
    public void Invalid_requests_never_consume_the_grant_or_write_a_vault(string json)
    {
        using var ws=new TempWorkspace();var path=Path.Combine(ws.Root,"target.bin");var receipt=Path.Combine(ws.Root,"receipt");
        var protocol=new ServiceMigrationProtocol(new(1,"owner",Migration),receipt,new(new SyntheticCrypto(),path));
        var response=JsonSerializer.Deserialize<ServiceMigrationProtocol.Response>(protocol.Dispatch(Encoding.UTF8.GetBytes(json)),ServiceRunAuthorization.Json)!;
        Assert.Equal("invalid-migration-request",response.Error);Assert.False(File.Exists(path));Assert.False(File.Exists(receipt));
    }
}
