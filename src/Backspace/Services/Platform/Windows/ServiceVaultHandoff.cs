using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

internal static class ServiceVaultHandoff
{
    /// <summary>The original vault remains locked and authoritative until the
    /// target has reread its persisted copy and activation succeeds. Never
    /// delete the original or replay an uncertain credential write.</summary>
    internal static void TransferVerifyAndActivate(SecretsStore source, string pipeName, string serviceSid,
        string migrationId, Action activate)
    {
        if (!Guid.TryParseExact(migrationId, "N", out _)) throw new ArgumentException("Invalid migration identity.");
        source.WithMigrationSnapshot(snapshot =>
        {
            var expected = ServiceMigrationProtocol.Digest(snapshot);
            try
            {
                Exchange(pipeName, serviceSid, new(1, "import", migrationId, new Dictionary<string, string>(snapshot))).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // A lost import reply may follow a successful write. Resolve
                // uncertainty with a separate read; never send the write again.
            }
            var verified = Exchange(pipeName, serviceSid, new(1, "verify", migrationId)).GetAwaiter().GetResult();
            if (verified.Version == 1 && verified.Error is null && verified.Digest is not null && verified.Count is not null
                && (verified.Count != snapshot.Count || verified.Digest != expected))
            {
                // Setup may have been cancelled before activation while the
                // original desktop vault subsequently received fresher tokens.
                // Conditional refresh is refused after activation, on a changed
                // target, or when either vault is unreadable. Never replay it.
                try
                {
                    Exchange(pipeName, serviceSid, new(1, "refresh", migrationId, new Dictionary<string, string>(snapshot),
                        verified.Digest, verified.Count)).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                verified = Exchange(pipeName, serviceSid, new(1, "verify", migrationId)).GetAwaiter().GetResult();
            }
            if (verified.Version != 1 || verified.Error is not null || verified.Count != snapshot.Count
                || !string.Equals(verified.Digest, expected, StringComparison.Ordinal))
                throw new IOException("The service credential copy could not be verified. Your original vault remains active and unchanged.");
            var accepted = Exchange(pipeName, serviceSid, new(1, "accept", migrationId,
                ExpectedDigest: expected, ExpectedCount: snapshot.Count)).GetAwaiter().GetResult();
            if (accepted.Version != 1 || accepted.Error is not null || accepted.Count != snapshot.Count || accepted.Digest != expected)
                throw new IOException("The service could not record the verified handoff. Your original vault has been preserved.");
            activate();
            return true;
        }, requireExisting: true);
    }

    private static async Task<ServiceMigrationProtocol.Response> Exchange(string pipeName, string serviceSid, ServiceMigrationProtocol.Request request)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json);
        byte[]? response = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var client = await BackupServicePipe.ConnectAsync(pipeName, serviceSid, TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false);
            await BackupServicePipe.WriteMessageAsync(client, payload, TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false);
            response = await BackupServicePipe.ReadResponseAsync(client, TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<ServiceMigrationProtocol.Response>(response, ServiceRunAuthorization.Json)
                ?? throw new InvalidDataException("The service returned no migration result.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (response is not null) CryptographicOperations.ZeroMemory(response);
        }
    }
}
