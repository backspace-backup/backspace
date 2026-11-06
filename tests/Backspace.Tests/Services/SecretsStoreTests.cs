using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

/// <summary>
/// The portable vault (U153): vault.bin travels with the folder, its key is
/// wrapped by the person's password, and each computer keeps its own copy of
/// that key. These tests pin the file format's refusals (wrong password,
/// tampered header, damaged body, newer format, a lone .bak), the store's
/// states and transactions, and that no value is ever readable on disk.
/// </summary>
public sealed class SecretsStoreTests
{
    private static VaultKeyCache Computer(string root) => new(Path.Combine(root, "keys"), "user", new DpapiSecretsEncryption());
    private static SecretsStore On(string root) => new(AppPaths.VaultFile, Computer(root));

    private static (byte[] Prefix, JsonNode Header, byte[] Body) Split(byte[] file)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8, 4));
        return (file[..8], JsonNode.Parse(file.AsSpan(12, length))!, file[(12 + length)..]);
    }

    private static byte[] Join(byte[] prefix, JsonNode header, byte[] body)
    {
        var h = Encoding.UTF8.GetBytes(header.ToJsonString());
        var output = new byte[12 + h.Length + body.Length];
        prefix.CopyTo(output, 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), h.Length);
        h.CopyTo(output, 12);
        body.CopyTo(output, 12 + h.Length);
        return output;
    }

    [Fact]
    public void Round_trip_reopens_with_this_computers_key_and_a_new_computer_needs_the_password()
    {
        using var ws = new TempWorkspace(vault: false);
        var here = On(ws.Sub("pc-a"));
        here.Create("correct horse");
        here.Set("dest:1:storagepw", "repo-password");
        Assert.Equal(VaultState.Unlocked, On(ws.Sub("pc-a")).Refresh());
        Assert.Equal("repo-password", On(ws.Sub("pc-a")).Get("dest:1:storagepw"));

        var newComputer = On(ws.Sub("pc-b"));
        Assert.Equal(VaultState.Locked, newComputer.Refresh());
        Assert.True(newComputer.TryUnlock("correct horse"));
        Assert.Equal("repo-password", newComputer.Get("dest:1:storagepw"));
        // Remembered on that computer from now on.
        Assert.Equal("repo-password", On(ws.Sub("pc-b")).Get("dest:1:storagepw"));
    }

    [Fact]
    public void Wrong_password_changes_nothing_and_keeps_no_key()
    {
        using var ws = new TempWorkspace(vault: false);
        On(ws.Sub("pc-a")).Create("right");
        var before = File.ReadAllBytes(AppPaths.VaultFile);
        var other = On(ws.Sub("pc-b"));
        Assert.False(other.TryUnlock("wrong"));
        Assert.False(other.TryUnlock(""));
        Assert.Equal(VaultState.Locked, other.Refresh());
        Assert.Equal(before, File.ReadAllBytes(AppPaths.VaultFile));
        Assert.False(Directory.Exists(Path.Combine(ws.Root, "pc-b", "keys")) && Directory.EnumerateFiles(Path.Combine(ws.Root, "pc-b", "keys")).Any());
    }

    [Fact]
    public void Empty_password_opens_on_a_new_computer_without_asking()
    {
        using var ws = new TempWorkspace(vault: false);
        var here = On(ws.Sub("pc-a"));
        here.Create("");
        here.Set("k", "v");
        var other = On(ws.Sub("pc-b"));
        Assert.Equal(VaultState.Unlocked, other.Refresh());
        Assert.True(other.NoPassword);
        Assert.Equal("v", other.Get("k"));
        // A password set later protects it: another new computer then asks.
        here.ChangePassword("now protected");
        Assert.Equal(VaultState.Locked, On(ws.Sub("pc-c")).Refresh());
    }

    [Fact]
    public void Tampered_key_derivation_settings_are_refused_and_preserved()
    {
        using var ws = new TempWorkspace(vault: false);
        On(ws.Sub("pc-a")).Create("pw");
        var (prefix, header, body) = Split(File.ReadAllBytes(AppPaths.VaultFile));
        // A different (still acceptable) iteration count: the wrap's check fails.
        header["kdf"]!["iter"] = VaultFile.MinimumIterations + 1;
        File.WriteAllBytes(AppPaths.VaultFile, Join(prefix, header, body));
        var tampered = File.ReadAllBytes(AppPaths.VaultFile);
        var other = On(ws.Sub("pc-b"));
        Assert.False(other.TryUnlock("pw"));
        Assert.Equal(tampered, File.ReadAllBytes(AppPaths.VaultFile));
        // Fewer iterations than allowed (a cheaper guess) is refused outright.
        header["kdf"]!["iter"] = VaultFile.MinimumIterations - 1;
        File.WriteAllBytes(AppPaths.VaultFile, Join(prefix, header, body));
        Assert.Equal(VaultState.Unreadable, On(ws.Sub("pc-c")).Refresh());
        // "No password" switched on by someone else does not open it either.
        header["kdf"]!["iter"] = VaultFile.Iterations;
        header["noPassword"] = true;
        File.WriteAllBytes(AppPaths.VaultFile, Join(prefix, header, body));
        Assert.Equal(VaultState.Locked, On(ws.Sub("pc-d")).Refresh());
    }

    [Fact]
    public void Damaged_body_is_reported_preserved_and_never_overwritten()
    {
        using var ws = new TempWorkspace(vault: false);
        var here = On(ws.Sub("pc-a"));
        here.Create("pw");
        here.Set("k", "v");
        var bytes = File.ReadAllBytes(AppPaths.VaultFile);
        bytes[^20] ^= 0x55;
        File.WriteAllBytes(AppPaths.VaultFile, bytes);

        var other = On(ws.Sub("pc-b"));
        Assert.False(other.TryUnlock("pw"));
        Assert.Equal(VaultState.Unreadable, other.State);
        Assert.Contains("damaged", other.LoadErrorMessage, StringComparison.OrdinalIgnoreCase);
        var ex = Assert.Throws<VaultLockedException>(() => other.Set("k2", "v2"));
        // Without a key, this computer cannot tell damage from a lock: it reports locked, and writes nothing.
        Assert.Equal(VaultState.Locked, ex.State);
        Assert.Equal(bytes, File.ReadAllBytes(AppPaths.VaultFile));
        // This computer's key no longer opens the body: treated as stale, removed, nothing written.
        Assert.Equal(VaultState.Locked, On(ws.Sub("pc-a")).Refresh());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(ws.Root, "pc-a", "keys"), "*.user.bin"));
        Assert.Equal(bytes, File.ReadAllBytes(AppPaths.VaultFile));
    }

    [Fact]
    public void A_newer_format_is_refused_and_kept()
    {
        using var ws = new TempWorkspace(vault: false);
        On(ws.Sub("pc-a")).Create("pw");
        var (prefix, header, body) = Split(File.ReadAllBytes(AppPaths.VaultFile));
        header["v"] = 2;
        var newer = Join(prefix, header, body);
        File.WriteAllBytes(AppPaths.VaultFile, newer);
        var store = On(ws.Sub("pc-a"));
        Assert.Equal(VaultState.Unreadable, store.Refresh());
        Assert.True(store.MadeByNewerVersion);
        Assert.Throws<VaultLockedException>(() => store.Set("k", "v"));
        Assert.Throws<VaultLockedException>(() => store.Get("k"));
        Assert.Throws<IOException>(() => store.Create("pw"));
        Assert.Equal(newer, File.ReadAllBytes(AppPaths.VaultFile));
    }

    [Fact]
    public void A_backup_without_its_main_file_is_refused_and_nothing_new_is_created()
    {
        using var ws = new TempWorkspace(vault: false);
        var store = On(ws.Sub("pc-a"));
        store.Create("pw");
        store.Set("k", "v1");
        store.Set("k", "v2");
        Assert.True(File.Exists(AppPaths.VaultFile + ".bak"));
        File.Delete(AppPaths.VaultFile);
        var after = On(ws.Sub("pc-a"));
        Assert.Equal(VaultState.Unreadable, after.Refresh());
        Assert.Contains(".bak", after.LoadErrorMessage);
        Assert.Throws<VaultLockedException>(() => after.Set("k", "v3"));
        Assert.Throws<IOException>(() => after.Create(""));
        Assert.False(File.Exists(AppPaths.VaultFile));
    }

    [Fact]
    public void Changing_the_password_rewrites_only_the_header_so_other_computers_keep_working()
    {
        using var ws = new TempWorkspace(vault: false);
        var a = On(ws.Sub("pc-a"));
        a.Create("first");
        a.Set("k", "v");
        var b = On(ws.Sub("pc-b"));
        Assert.True(b.TryUnlock("first"));
        var bodyBefore = Split(File.ReadAllBytes(AppPaths.VaultFile)).Body;

        a.ChangePassword("second");
        Assert.Equal(bodyBefore, Split(File.ReadAllBytes(AppPaths.VaultFile)).Body);
        Assert.Equal("v", On(ws.Sub("pc-b")).Get("k"));
        var c = On(ws.Sub("pc-c"));
        Assert.False(c.TryUnlock("first"));
        Assert.True(c.TryUnlock("second"));
    }

    [Fact]
    public void Key_copies_are_protected_and_tied_to_their_vault()
    {
        using var ws = new TempWorkspace(vault: false);
        var cache = Computer(ws.Sub("pc-a"));
        var id = VaultFile.NewId();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(VaultFile.KeyBytes);
        cache.Write(id, key);
        Assert.Equal(key, cache.TryRead(id));
        var stored = File.ReadAllBytes(cache.PathFor(id));
        Assert.False(stored.AsSpan().IndexOf(key) >= 0, "the key copy must not hold the key in the clear");
        Assert.Null(cache.TryRead(VaultFile.NewId()));
        // Copied under another vault's name, the protection (bound to the id) refuses it.
        var otherId = VaultFile.NewId();
        File.Copy(cache.PathFor(id), cache.PathFor(otherId));
        Assert.Null(cache.TryRead(otherId));
        Assert.Throws<ArgumentException>(() => cache.PathFor("..\\x"));
    }

    [Fact]
    public void A_stale_key_copy_is_removed_and_the_vault_counts_as_locked()
    {
        using var ws = new TempWorkspace(vault: false);
        var a = On(ws.Sub("pc-a"));
        a.Create("pw");
        var id = a.VaultId!;
        Computer(ws.Sub("pc-a")).Write(id, new byte[VaultFile.KeyBytes]);
        var reopened = On(ws.Sub("pc-a"));
        Assert.Equal(VaultState.Locked, reopened.Refresh());
        Assert.False(File.Exists(Computer(ws.Sub("pc-a")).PathFor(id)));
        Assert.True(reopened.TryUnlock("pw"));
    }

    [Fact]
    public void Saving_or_reading_while_locked_changes_nothing_and_says_locked_not_missing()
    {
        using var ws = new TempWorkspace(vault: false);
        var a = On(ws.Sub("pc-a"));
        a.Create("pw");
        a.Set("k", "v");
        var before = File.ReadAllBytes(AppPaths.VaultFile);
        var b = On(ws.Sub("pc-b"));
        var set = Assert.Throws<VaultLockedException>(() => b.Set("k", "other"));
        Assert.Equal(VaultState.Locked, set.State);
        Assert.Throws<VaultLockedException>(() => b.PutNew("p", "x"));
        Assert.Throws<VaultLockedException>(() => b.Remove("k"));
        var get = Assert.Throws<VaultLockedException>(() => b.TryGet("k", out _));
        Assert.Contains("enter your password once", get.Message);
        Assert.Equal(before, File.ReadAllBytes(AppPaths.VaultFile));
    }

    [Fact]
    public void Without_a_vault_reads_find_nothing_and_saving_needs_a_password_first()
    {
        using var ws = new TempWorkspace(vault: false);
        var store = new SecretsStore();
        Assert.Equal(VaultState.NoVault, store.Refresh());
        Assert.False(store.TryGet("k", out _));
        Assert.Equal("", store.Get("k"));
        Assert.Equal(VaultState.NoVault, Assert.Throws<VaultLockedException>(() => store.Set("k", "v")).State);
        Assert.False(File.Exists(AppPaths.VaultFile));
    }

    [Fact]
    public void Earlier_files_without_a_vault_block_reads_until_they_are_moved()
    {
        using var ws = new TempWorkspace(vault: false);
        File.WriteAllBytes(AppPaths.LegacySecretsFile, new byte[] { 1, 2, 3 });
        var store = new SecretsStore();
        Assert.Equal(VaultState.NoVault, store.Refresh());
        Assert.True(store.ConversionPending);
        var ex = Assert.Throws<VaultLockedException>(() => store.Get("k"));
        Assert.Contains("choose a password", ex.Message);
    }

    [Fact]
    public void Two_instances_keep_both_writers_keys()
    {
        using var ws = new TempWorkspace();
        var first = new SecretsStore();
        var second = new SecretsStore();
        Assert.Equal(VaultState.Unlocked, first.Refresh());
        Assert.Equal(VaultState.Unlocked, second.Refresh());
        first.Set("a", "1");
        second.Set("b", "2");
        Parallel.For(0, 8, i => new SecretsStore().Set("p" + i, i.ToString()));
        var reopened = new SecretsStore();
        Assert.Equal("1", reopened.Get("a"));
        Assert.Equal("2", reopened.Get("b"));
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(i.ToString(), reopened.Get("p" + i)));
    }

    [Fact]
    public void SetIfNewer_compares_inside_one_transaction()
    {
        using var ws = new TempWorkspace();
        var store = new SecretsStore();
        static string Token(string expiry) => $"{{\"access_token\":\"a\",\"expiry\":\"{expiry}\"}}";
        store.Set("oauth", Token("2026-01-01T00:00:00Z"));
        Assert.True(store.SetIfNewer("oauth", Token("2026-02-01T00:00:00Z"), RcloneConfigBuilder.IsNewerToken));
        // Another process saved a newer sign-in meanwhile: an older candidate loses.
        new SecretsStore().Set("oauth", Token("2026-04-01T00:00:00Z"));
        Assert.False(store.SetIfNewer("oauth", Token("2026-03-01T00:00:00Z"), RcloneConfigBuilder.IsNewerToken));
        Assert.Equal(Token("2026-04-01T00:00:00Z"), new SecretsStore().Get("oauth"));
        var calls = 0;
        Assert.False(store.SetIfNewer("oauth", "x", (candidate, current) => { calls++; return false; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Starting_over_keeps_the_damaged_file_and_creates_an_empty_vault()
    {
        using var ws = new TempWorkspace(vault: false);
        File.WriteAllBytes(AppPaths.VaultFile, Encoding.ASCII.GetBytes("not a vault"));
        var store = new SecretsStore();
        Assert.Equal(VaultState.Unreadable, store.Refresh());
        store.StartOver("fresh");
        Assert.Equal(VaultState.Unlocked, store.Refresh());
        var kept = Assert.Single(Directory.GetFiles(ws.ConfigRoot, "vault.bin.unreadable-*"));
        Assert.Equal("not a vault", File.ReadAllText(kept));
        Assert.Throws<InvalidOperationException>(() => store.StartOver("again"));
    }

    [Fact]
    public void No_value_or_key_is_readable_in_any_file()
    {
        using var ws = new TempWorkspace(vault: false);
        var store = new SecretsStore();
        store.Create("pw");
        const string value = "Storage-Password-Visible-7731";
        store.Set("dest:x:storagepw", value);
        var (id, key) = store.ExportKey();
        foreach (var file in Directory.EnumerateFiles(ws.Root, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) < 0, file);
            Assert.True(bytes.AsSpan().IndexOf(key) < 0, file);
        }
        Assert.Equal(id, store.VaultId);
    }
}
