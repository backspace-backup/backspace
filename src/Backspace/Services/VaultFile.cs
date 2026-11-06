using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backspace.Services;

/// <summary>
/// The portable credential vault, <c>Backspace.config\vault.bin</c> (U153).
///
/// <para>Layout: the 8-byte marker <c>BSVAULT1</c>, a 4-byte little-endian
/// header length, a UTF-8 JSON header, then the body: a 12-byte nonce, the
/// AES-256-GCM ciphertext of the JSON credential dictionary and its 16-byte
/// tag.</para>
///
/// <para>A random 32-byte vault key encrypts the body. The header carries that
/// key wrapped (AES-256-GCM) by a key derived from the person's password with
/// PBKDF2-HMAC-SHA256. An empty password is allowed: the header then says
/// <c>noPassword</c> and the credentials are only obscured. Changing the
/// password rewrites the header alone, so every computer's copy of the vault
/// key (<see cref="VaultKeyCache"/>) keeps working.</para>
/// </summary>
internal static class VaultFile
{
    internal const string FileName = "vault.bin";
    internal const int FormatVersion = 1;
    internal const int KeyBytes = 32;
    private const int NonceBytes = 12, TagBytes = 16, SaltBytes = 16;
    private static readonly byte[] Marker = "BSVAULT1"u8.ToArray();
    private const int MaxHeaderBytes = 64 * 1024;
    internal const string Algorithm = "pbkdf2-sha256";

    /// <summary>PBKDF2 iterations for new vaults. OWASP's 2023 figure for
    /// PBKDF2-HMAC-SHA256. Tests lower it (with <see cref="MinimumIterations"/>)
    /// so the suite does not spend half a second per vault.</summary>
    internal static int Iterations { get; set; } = 600_000;
    /// <summary>Headers asking for fewer iterations are refused as damaged:
    /// someone who edits the header must not make a guess cheaper.</summary>
    internal static int MinimumIterations { get; set; } = 100_000;
    internal const int MaximumIterations = 20_000_000;

    internal sealed class Header
    {
        [JsonPropertyName("v")] public int V { get; set; }
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("kdf")] public Kdf KdfParams { get; set; } = new();
        [JsonPropertyName("noPassword")] public bool NoPassword { get; set; }
        [JsonPropertyName("wrap")] public Wrap Wrapped { get; set; } = new();
    }
    internal sealed class Kdf
    {
        [JsonPropertyName("alg")] public string Alg { get; set; } = "";
        [JsonPropertyName("iter")] public int Iter { get; set; }
        [JsonPropertyName("salt")] public string Salt { get; set; } = "";
    }
    internal sealed class Wrap
    {
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("ct")] public string Ct { get; set; } = "";
        [JsonPropertyName("tag")] public string Tag { get; set; } = "";
    }

    /// <summary>A parsed file: its header and the encrypted body, still sealed.</summary>
    internal sealed record Parsed(Header Header, byte[] HeaderBytes, byte[] Body)
    {
        internal string Id => Header.Id;
    }

    /// <summary>The file is not a vault this version can open: wrong marker,
    /// broken header, or made by a newer Backspace.</summary>
    internal sealed class DamagedException(string message, bool newer = false) : IOException(message)
    {
        internal bool Newer { get; } = newer;
    }

    internal static Parsed Parse(byte[] bytes)
    {
        if (bytes.Length < Marker.Length + 4 || !bytes.AsSpan(0, Marker.Length).SequenceEqual(Marker))
            throw new DamagedException("This file is not a Backspace password vault.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Marker.Length, 4));
        if (length is <= 0 or > MaxHeaderBytes || Marker.Length + 4 + length > bytes.Length)
            throw new DamagedException("The saved passwords file is damaged (its header is cut short).");
        var headerBytes = bytes.AsSpan(Marker.Length + 4, length).ToArray();
        Header header;
        try
        {
            // A version check first, before the rest of the shape: a newer
            // format may have changed it.
            using (var doc = JsonDocument.Parse(headerBytes))
            {
                if (!doc.RootElement.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number)
                    throw new DamagedException("The saved passwords file is damaged (its header has no version).");
                if (v.GetInt32() > FormatVersion)
                    throw new DamagedException("These saved passwords were made by a newer version of Backspace. Use that version, or update this one.", newer: true);
            }
            header = JsonSerializer.Deserialize<Header>(headerBytes)
                ?? throw new DamagedException("The saved passwords file is damaged (empty header).");
        }
        catch (JsonException ex) { throw new DamagedException("The saved passwords file is damaged (" + ex.Message + ")."); }
        if (header.V != FormatVersion || !IsId(header.Id) || header.KdfParams.Alg != Algorithm
            || header.KdfParams.Iter < MinimumIterations || header.KdfParams.Iter > MaximumIterations
            || !TryBase64(header.KdfParams.Salt, SaltBytes, out _)
            || !TryBase64(header.Wrapped.Nonce, NonceBytes, out _) || !TryBase64(header.Wrapped.Ct, KeyBytes, out _)
            || !TryBase64(header.Wrapped.Tag, TagBytes, out _))
            throw new DamagedException("The saved passwords file is damaged (its header was changed).");
        var body = bytes.AsSpan(Marker.Length + 4 + length).ToArray();
        if (body.Length < NonceBytes + TagBytes)
            throw new DamagedException("The saved passwords file is damaged (its contents are cut short).");
        return new Parsed(header, headerBytes, body);
    }

    internal static bool IsId(string? id) => id is { Length: 32 } && id.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c));

    private static bool TryBase64(string value, int expected, out byte[] bytes)
    {
        bytes = [];
        try { bytes = Convert.FromBase64String(value); }
        catch (System.FormatException) { return false; }
        return bytes.Length == expected;
    }

    private static byte[] WrapAad(Header header) => Encoding.UTF8.GetBytes(
        $"BSVAULT1|wrap|{header.Id}|{header.KdfParams.Alg}|{header.KdfParams.Iter}|{header.KdfParams.Salt}|{(header.NoPassword ? 1 : 0)}");
    private static byte[] BodyAad(string id) => Encoding.UTF8.GetBytes($"BSVAULT1|body|{id}|v1");

    internal static byte[] DeriveKek(string password, byte[] salt, int iterations)
    {
        var normalized = (password ?? "").Normalize(NormalizationForm.FormC);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, KeyBytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>The vault key, or null when the password is wrong (the wrap's
    /// tag fails). Nothing is written either way.</summary>
    internal static byte[]? Unwrap(Parsed parsed, string password)
    {
        var header = parsed.Header;
        var kek = DeriveKek(password, Convert.FromBase64String(header.KdfParams.Salt), header.KdfParams.Iter);
        var key = new byte[KeyBytes];
        try
        {
            using var gcm = new AesGcm(kek, TagBytes);
            gcm.Decrypt(Convert.FromBase64String(header.Wrapped.Nonce), Convert.FromBase64String(header.Wrapped.Ct),
                Convert.FromBase64String(header.Wrapped.Tag), key, WrapAad(header));
            return key;
        }
        catch (AuthenticationTagMismatchException) { CryptographicOperations.ZeroMemory(key); return null; }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>The credentials, or null when this key does not open the body.</summary>
    internal static Dictionary<string, string>? OpenBody(Parsed parsed, byte[] key)
    {
        if (key.Length != KeyBytes) return null;
        var body = parsed.Body;
        var clear = new byte[body.Length - NonceBytes - TagBytes];
        try
        {
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Decrypt(body.AsSpan(0, NonceBytes), body.AsSpan(NonceBytes, clear.Length),
                body.AsSpan(body.Length - TagBytes), clear, BodyAad(parsed.Id));
        }
        catch (AuthenticationTagMismatchException) { CryptographicOperations.ZeroMemory(clear); return null; }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(clear)
                ?? throw new DamagedException("The saved passwords file is damaged (no credential list).");
        }
        catch (JsonException) { throw new DamagedException("The saved passwords file is damaged (its credential list cannot be read)."); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    internal static byte[] SealBody(string id, byte[] key, IReadOnlyDictionary<string, string> map)
    {
        var clear = JsonSerializer.SerializeToUtf8Bytes(map);
        try
        {
            var output = new byte[NonceBytes + clear.Length + TagBytes];
            RandomNumberGenerator.Fill(output.AsSpan(0, NonceBytes));
            using var gcm = new AesGcm(key, TagBytes);
            gcm.Encrypt(output.AsSpan(0, NonceBytes), clear, output.AsSpan(NonceBytes, clear.Length),
                output.AsSpan(NonceBytes + clear.Length), BodyAad(id));
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    /// <summary>A header wrapping <paramref name="key"/> under a new salt.</summary>
    internal static byte[] NewHeader(string id, byte[] key, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var header = new Header
        {
            V = FormatVersion, Id = id, NoPassword = string.IsNullOrEmpty(password),
            KdfParams = new Kdf { Alg = Algorithm, Iter = Iterations, Salt = Convert.ToBase64String(salt) },
        };
        var kek = DeriveKek(password, salt, header.KdfParams.Iter);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var ct = new byte[KeyBytes];
            var tag = new byte[TagBytes];
            using var gcm = new AesGcm(kek, TagBytes);
            gcm.Encrypt(nonce, key, ct, tag, WrapAad(header));
            header.Wrapped = new Wrap { Nonce = Convert.ToBase64String(nonce), Ct = Convert.ToBase64String(ct), Tag = Convert.ToBase64String(tag) };
            return JsonSerializer.SerializeToUtf8Bytes(header);
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    internal static byte[] Compose(byte[] headerBytes, byte[] body)
    {
        var output = new byte[Marker.Length + 4 + headerBytes.Length + body.Length];
        Marker.CopyTo(output, 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(Marker.Length, 4), headerBytes.Length);
        headerBytes.CopyTo(output, Marker.Length + 4);
        body.CopyTo(output, Marker.Length + 4 + headerBytes.Length);
        return output;
    }

    internal static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Order-independent digest of a credential map, used to compare
    /// two copies without revealing a value (counts and digests only).</summary>
    internal static string Digest(IReadOnlyDictionary<string, string> map)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in map.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var k = Encoding.UTF8.GetBytes(pair.Key);
            var v = Encoding.UTF8.GetBytes(pair.Value ?? "");
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, k.Length); hash.AppendData(len); hash.AppendData(k);
            BinaryPrimitives.WriteInt32LittleEndian(len, v.Length); hash.AppendData(len); hash.AppendData(v);
            CryptographicOperations.ZeroMemory(v);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
