using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>One-time vault handoff allowed only by an administrator-protected
/// installation grant. The target identity encrypts the received snapshot.
/// Neither the grant nor the durable receipt contains credentials or their hash.</summary>
internal sealed class ServiceMigrationProtocol
{
    internal const string GrantFileName = "service-migration.json";
    internal sealed record Grant(int Version, string OwnerSid, string MigrationId);
    internal sealed record Request(int Version, string Command, string MigrationId, Dictionary<string, string>? Secrets = null,
        string? ExpectedDigest = null, int? ExpectedCount = null);
    internal sealed record Response(int Version, string? Digest = null, int? Count = null, string? Error = null);
    internal sealed record Acceptance(int Version, string MigrationId);
    private readonly Grant _grant;
    private readonly string _receipt;
    private readonly SecretsStore _vault;
    private readonly object _gate = new();
    private readonly Func<IDisposable>? _beginRefresh;

    internal ServiceMigrationProtocol(Grant grant, string receipt, SecretsStore vault, Func<IDisposable>? beginRefresh = null)
    { _grant = grant; _receipt = receipt; _vault = vault; _beginRefresh = beginRefresh; }

    internal static ServiceMigrationProtocol? Load(string installation, ServiceWorkerBinding binding, SecretsStore vault)
    {
        var path = ServicePortableLayout.ExistingRecord(installation, GrantFileName);
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ServiceInstallationSecurity.ValidateProtectedRecord(path, installation);
        if (file.Length is <= 0 or > 8192) throw new InvalidDataException("Invalid service migration grant.");
        var grant = JsonSerializer.Deserialize<Grant>(file, ServiceRunAuthorization.Json)
            ?? throw new InvalidDataException("Missing migration grant.");
        if (grant.Version != 1 || grant.OwnerSid != binding.OwnerSid || !Guid.TryParseExact(grant.MigrationId, "N", out _))
            throw new SecurityException("Invalid migration identity.");
        return new(grant, Path.Combine(binding.DataDirectory, "migration-" + grant.MigrationId + ".receipt"), vault, () =>
        {
            var lease = ServiceCoordinationFiles.TryOpen(new(installation, binding), ServiceCoordinationFiles.MigrationFile, true)
                ?? throw new IOException("Service activation is already running.");
            try
            {
                if (ServiceWorkerBinding.Load(installation).CanRun)
                    throw new IOException("The active service vault cannot be replaced by an earlier desktop copy.");
                return lease;
            }
            catch { lease.Dispose(); throw; }
        });
    }

    internal byte[] Dispatch(byte[] bytes)
    {
        // Keep exception filters outside the method that owns the monitor.
        // The optimized compiler used by the release build reused the lock's
        // cleanup flag in a rejected filter, leaving the monitor held. The
        // cross-thread retry regression exercises the previously hanging path.
        try { return DispatchCore(bytes); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        { return Error("invalid-migration-request"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { return Error("migration-needs-recovery"); }
    }

    private byte[] DispatchCore(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > BackupServicePipe.MaximumMessageBytes) throw new InvalidDataException();
        var reader = new Utf8JsonReader(bytes);
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw new JsonException();
        }
        var request = JsonSerializer.Deserialize<Request>(bytes, ServiceRunAuthorization.Json) ?? throw new JsonException();
        if (request.Version != 1 || request.MigrationId != _grant.MigrationId || request.Command is not ("import" or "verify" or "accept" or "refresh"))
            throw new InvalidDataException();
        if (request.Command is not ("import" or "refresh") && request.Secrets is not null) throw new InvalidDataException();
        if (request.Command is "accept" or "refresh")
        {
            if (request.ExpectedCount is null or < 0 or > 4096 || request.ExpectedDigest is null
                || request.ExpectedDigest.Length != 64 || request.ExpectedDigest.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException();
        }
        else if (request.ExpectedDigest is not null || request.ExpectedCount is not null) throw new InvalidDataException();
        if ((request.Command is "import" or "refresh") && (request.Secrets is null || request.Secrets.Count > 4096
            || request.Secrets.Any(p => string.IsNullOrEmpty(p.Key) || p.Key.Length > 1024 || p.Value is null || p.Value.Length > 128 * 1024)))
            throw new InvalidDataException();
        Progress("validated-" + request.Command);
        lock (_gate)
        {
            Progress("locked-" + request.Command);
            if (request.Command == "import")
            {
                // Reserve the grant durably before import. A crash leaves
                // the source authoritative and requires explicit recovery;
                // it must not allow a missing vault to be silently reseeded.
                Progress("reserving-import");
                using (var receipt = new FileStream(_receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { receipt.WriteByte(1); receipt.Flush(true); }
                _vault.ImportIntoNewVault(request.Secrets!);
            }
            else if (!File.Exists(_receipt)) throw new InvalidDataException();
            if (request.Command == "refresh")
            {
                // Only an inactive, already readable target can be refreshed.
                // Hold the same protected OS lock as administrator activation.
                using var lease = _beginRefresh?.Invoke() ?? throw new IOException("Inactive handoff recovery is unavailable.");
                _vault.WithMigrationSnapshot(map =>
                {
                    if (map.Count != request.ExpectedCount || Digest(map) != request.ExpectedDigest)
                        throw new IOException("The target changed after verification.");
                    // Invalidate acceptance BEFORE any possible replacement.
                    // A crash can leave work to retry, never permission to
                    // activate a credential copy that was not reread afterward.
                    File.Delete(_receipt + ".accepted");
                    _vault.ReplaceMigrationCopy(request.Secrets!, current =>
                        current.Count == request.ExpectedCount && Digest(current) == request.ExpectedDigest);
                    return true;
                }, requireExisting: true);
            }
            Progress("opening-vault-" + request.Command);
            var response = _vault.WithMigrationSnapshot(map =>
            {
                Progress("vault-opened-" + request.Command);
                var digest = Digest(map);
                if (request.Command == "accept")
                {
                    if (request.ExpectedCount != map.Count || request.ExpectedDigest != digest)
                        throw new InvalidDataException("The persisted credential copy changed before acceptance.");
                    // This private receipt contains no credential values or
                    // hashes. Administrator activation requires it; merely
                    // starting import never authorizes a scheduled backup.
                    var accepted = _receipt + ".accepted";
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new Acceptance(1, _grant.MigrationId), ServiceRunAuthorization.Json);
                    var temporary = accepted + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { file.Write(bytes); file.Flush(true); }
                        File.Move(temporary, accepted, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                return new Response(1, digest, map.Count);
            }, requireExisting: true);
            return JsonSerializer.SerializeToUtf8Bytes(response, ServiceRunAuthorization.Json);
        }
    }

    internal static string Digest(IReadOnlyDictionary<string, string> map)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SortedDictionary<string, string>(map.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal));
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Error(string error) => JsonSerializer.SerializeToUtf8Bytes(new Response(1, Error: error), ServiceRunAuthorization.Json);

    internal async Task ServeAsync(string name, string serviceSid, string ownerSid, CancellationToken stop)
    {
        using var listener = BackupServicePipe.CreateListener(name, serviceSid, ownerSid);
        while (!stop.IsCancellationRequested)
        {
            Progress("waiting-for-connection");
            await listener.WaitForConnectionAsync(stop).ConfigureAwait(false);
            byte[]? request = null, response = null;
            try
            {
                Progress("reading-request");
                request = await BackupServicePipe.ReadMessageAsync(listener, TimeSpan.FromSeconds(15), stop).ConfigureAwait(false);
                Progress("processing-request");
                response = Dispatch(request);
                Progress("sending-response");
                await BackupServicePipe.SendResponseAsync(listener, response, TimeSpan.FromSeconds(15), stop).ConfigureAwait(false);
                Progress("response-acknowledged");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { Progress("connection-interrupted"); }
            finally
            {
                if (request is not null) CryptographicOperations.ZeroMemory(request);
                if (response is not null) CryptographicOperations.ZeroMemory(response);
                Progress("disconnecting");
                listener.Disconnect();
            }
        }
    }

    private void Progress(string stage)
    {
        // Bounded diagnostic metadata, never request fields, credentials or
        // digests. A diagnostic write cannot make the handoff fail.
        try { File.WriteAllBytes(_receipt + ".progress", JsonSerializer.SerializeToUtf8Bytes(new { stage, utc = DateTime.UtcNow })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
