using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>Return a frozen service vault to its ordinary owner. This endpoint
/// exists only after administrator retirement preparation. It cannot modify
/// either vault or choose any path; acceptance is a private durable receipt.</summary>
internal sealed class ServiceVaultReturnProtocol(string returnId, string receiptPath, SecretsStore vault)
{
    internal sealed record Request(int Version, string Command, string ReturnId,
        string? ExpectedDigest = null, int? ExpectedCount = null, string? OwnerCiphertextSha256 = null);
    internal sealed record Response(int Version, Dictionary<string, string>? Secrets = null, string? Error = null);
    internal sealed record Receipt(int Version, string ReturnId, string OwnerCiphertextSha256);

    internal byte[] Dispatch(byte[] bytes)
    {
        try
        {
            if (bytes.Length is <= 0 or > 4096) throw new InvalidDataException();
            ServiceRunAuthorization.RejectDuplicateProperties(bytes);
            var request = JsonSerializer.Deserialize<Request>(bytes, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
            if (request.Version != 1 || request.ReturnId != returnId || !Guid.TryParseExact(returnId, "N", out _)
                || request.Command is not ("export" or "accept")) throw new InvalidDataException();
            if (request.Command == "export")
            {
                if (request.ExpectedDigest is not null || request.ExpectedCount is not null || request.OwnerCiphertextSha256 is not null)
                    throw new InvalidDataException();
            }
            else if (!Hash(request.ExpectedDigest) || !Hash(request.OwnerCiphertextSha256) || request.ExpectedCount is null or < 0 or > 4096)
                throw new InvalidDataException();
            return vault.WithMigrationSnapshot(map =>
            {
                if (request.Command == "export")
                {
                    var response = JsonSerializer.SerializeToUtf8Bytes(new Response(1, new(map)), ServiceRunAuthorization.Json);
                    if (response.Length > BackupServicePipe.MaximumMessageBytes)
                    { CryptographicOperations.ZeroMemory(response); throw new IOException("The vault is too large to return in one verified exchange."); }
                    return response;
                }
                if (map.Count != request.ExpectedCount || ServiceMigrationProtocol.Digest(map) != request.ExpectedDigest)
                    throw new IOException("The returned credentials do not match the service vault.");
                // The service remains frozen even after acceptance. The
                // administrator still has to verify the local encrypted file
                // and remove registration before the owner provider switches.
                var receipt = JsonSerializer.SerializeToUtf8Bytes(new Receipt(1, returnId, request.OwnerCiphertextSha256!), ServiceRunAuthorization.Json);
                var temporary = receiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(receipt); file.Flush(true); }
                    File.Move(temporary, receiptPath, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return JsonSerializer.SerializeToUtf8Bytes(new Response(1), ServiceRunAuthorization.Json);
            }, requireExisting: true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        { return Error("invalid-return-request"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        { return Error("return-needs-recovery"); }
    }

    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static byte[] Error(string code) => JsonSerializer.SerializeToUtf8Bytes(new Response(1, Error: code), ServiceRunAuthorization.Json);

    internal async Task ServeAsync(ServiceWorkerBinding binding, CancellationToken stop)
    {
        using var listener = BackupServicePipe.CreateListener(binding.ServiceName + "-return", binding.ServiceSid, binding.OwnerSid);
        while (!stop.IsCancellationRequested)
        {
            await listener.WaitForConnectionAsync(stop).ConfigureAwait(false);
            byte[]? request = null, response = null;
            try
            {
                request = await BackupServicePipe.ReadMessageAsync(listener, TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
                response = Dispatch(request);
                await BackupServicePipe.SendResponseAsync(listener, response, TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { }
            finally
            {
                if (request is not null) CryptographicOperations.ZeroMemory(request);
                if (response is not null) CryptographicOperations.ZeroMemory(response);
                listener.Disconnect();
            }
        }
    }

    internal static Response Exchange(ServiceWorkerBinding binding, Request request)
        => ExchangeAsync(binding, request).GetAwaiter().GetResult();
    private static async Task<Response> ExchangeAsync(ServiceWorkerBinding binding, Request request)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-return", binding.ServiceSid, TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json);
        byte[]? response = null;
        try
        {
            await BackupServicePipe.WriteMessageAsync(pipe, payload, TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
            response = await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(10), stop.Token).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<Response>(response, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
            if (parsed.Version != 1 || parsed.Error is not null || (request.Command == "export") != (parsed.Secrets is not null))
                throw new IOException("The service credentials could not be returned and verified. Both copies have been preserved.");
            return parsed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (response is not null) CryptographicOperations.ZeroMemory(response);
        }
    }
}

internal static class ServiceVaultReturn
{
    /// <summary>The caller holds the ordinary vault transaction from before
    /// freezing the service until the elevated removal has finished.</summary>
    internal static void Transfer(SecretsStore owner, string ownerPath, string returnId,
        Func<ServiceVaultReturnProtocol.Request, ServiceVaultReturnProtocol.Response> exchange,
        Action finishRemoval)
    {
        owner.WithMigrationSnapshot(previous =>
        {
            var expectedOwner = ServiceMigrationProtocol.Digest(previous);
            var exported = exchange(new(1, "export", returnId));
            if (exported.Version != 1 || exported.Error is not null || exported.Secrets is null)
                throw new IOException("The frozen service vault could not be read. Its credentials have been preserved.");
            var map = exported.Secrets;
            var digest = ServiceMigrationProtocol.Digest(map);
            // Preserve .bak, publish atomically, and reread through the owner's
            // actual encryption provider. No successful write alone is proof.
            // A retry after declined elevation already has the current map.
            // Keep its encrypted bytes and the pre-return .bak unchanged.
            if (expectedOwner != digest)
                owner.ReplaceMigrationCopy(map, current => ServiceMigrationProtocol.Digest(current) == expectedOwner);
            owner.WithMigrationSnapshot(saved =>
            {
                if (saved.Count != map.Count || ServiceMigrationProtocol.Digest(saved) != digest)
                    throw new CryptographicException("The returned credentials failed verification. Service removal was not authorized.");
                using var held = new FileStream(ownerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var ciphertextHash = Convert.ToHexString(SHA256.HashData(held));
                var accepted = exchange(new(1, "accept", returnId, digest, saved.Count, ciphertextHash));
                if (accepted.Version != 1 || accepted.Error is not null || accepted.Secrets is not null)
                    throw new IOException("The service has not accepted the verified credential return. Its vault has been retained.");
                // Keep both the transaction and this exact encrypted file
                // held while the elevated helper validates its hash.
                finishRemoval();
                return true;
            }, requireExisting: true);
            return true;
        }, requireExisting: true);
    }
}
