using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>Credential operations over the authenticated local service pipe.
/// Endpoint identity comes from installation metadata, never a request payload.
/// There is no retry of a write: a lost reply is an uncertain result, not
/// permission to overwrite a newer refresh token by replaying the request.</summary>
internal sealed class ServiceSecretsBackend(string pipeName, string serviceSid) : ISecretsBackend
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public void ValidateReadable() => Call(new() { Command = "validate" });
    public bool TryGet(string key, out string value)
    {
        var response = Call(new() { Command = "get", Key = key });
        value = response.Value ?? "";
        return response.Found;
    }
    public void Set(string key, string value) => Call(new() { Command = "set", Key = key, Value = value });
    public void Remove(string key) => Call(new() { Command = "remove", Key = key });

    private static byte[] Encode(ServiceSecretsProtocol.Request request)
    {
        ServiceSecretsProtocol.Validate(request);
        return JsonSerializer.SerializeToUtf8Bytes(request, ServiceSecretsProtocol.Json);
    }

    private ServiceSecretsProtocol.Response Call(ServiceSecretsProtocol.Request request)
    {
        var payload = Encode(request);
        try { return ExchangeAsync(payload).GetAwaiter().GetResult(); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private async Task<ServiceSecretsProtocol.Response> ExchangeAsync(byte[] payload)
    {
        using var deadline = new CancellationTokenSource(Timeout);
        using var client = await BackupServicePipe.ConnectAsync(pipeName, serviceSid, Timeout, deadline.Token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(client, payload, Timeout, deadline.Token).ConfigureAwait(false);
        var bytes = await BackupServicePipe.ReadResponseAsync(client, Timeout, deadline.Token).ConfigureAwait(false);
        try
        {
            var response = JsonSerializer.Deserialize<ServiceSecretsProtocol.Response>(bytes, ServiceSecretsProtocol.Json)
                ?? throw new InvalidDataException("The backup service returned an empty response.");
            if (response.Version != 1) throw new InvalidDataException("The backup service uses an incompatible credential protocol.");
            if (response.Error is not null)
                throw new IOException(response.Error switch
                {
                    "vault-unavailable" => "The backup service could not unlock its saved credentials. Its encrypted vault has been preserved.",
                    "invalid-request" => "The backup service rejected the credential request.",
                    _ => "The backup service could not complete the credential request. No local fallback was used."
                });
            return response;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

/// <summary>Strict, bounded vocabulary: no path, executable, arbitrary command,
/// vault export or installer operations are accepted on the credential endpoint.</summary>
internal static class ServiceSecretsProtocol
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };
    internal sealed class Request
    {
        public int Version { get; set; } = 1;
        public string Command { get; set; } = "";
        public string? Key { get; set; }
        public string? Value { get; set; }
    }
    internal sealed class Response
    {
        public int Version { get; set; } = 1;
        public bool Found { get; set; }
        public string? Value { get; set; }
        public string? Error { get; set; }
    }

    internal static void Validate(Request request)
    {
        if (request.Version != 1 || request.Command is not ("validate" or "get" or "set" or "remove"))
            throw new InvalidDataException("Unsupported credential request.");
        if (request.Command == "validate")
        {
            if (request.Key is not null || request.Value is not null) throw new InvalidDataException("Unexpected credential fields.");
        }
        else
        {
            if (string.IsNullOrEmpty(request.Key) || request.Key.Length > 1024)
                throw new InvalidDataException("Invalid credential reference.");
            if (request.Command == "set")
            {
                if (request.Value is null || request.Value.Length > 128 * 1024)
                    throw new InvalidDataException("Invalid credential value.");
            }
            else if (request.Value is not null) throw new InvalidDataException("Unexpected credential value.");
        }
    }

    internal static byte[] Dispatch(ReadOnlySpan<byte> payload, SecretsStore vault, Func<bool>? writesFrozen = null)
    {
        try
        {
            if (payload.Length is <= 0 or > BackupServicePipe.MaximumMessageBytes)
                throw new InvalidDataException("Invalid credential request length.");
            // System.Text.Json otherwise accepts repeated properties with the
            // last value winning. Reject ambiguity at the trust boundary.
            var reader = new Utf8JsonReader(payload);
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                if (reader.TokenType == JsonTokenType.PropertyName && !names.Add(reader.GetString()!))
                    throw new JsonException("Duplicate request property.");
            var request = JsonSerializer.Deserialize<Request>(payload, Json)
                ?? throw new JsonException("Missing request.");
            Validate(request);
            // Keep the read check and write under one cross-process vault lock.
            // An unreadable vault must never be reset by an automatic refresh.
            var response = vault.WithMigrationSnapshot(map =>
            {
                if (request.Command is "set" or "remove" && writesFrozen?.Invoke() == true)
                    throw new IOException("The service vault is frozen for its verified return to the owner.");
                var result = new Response();
                switch (request.Command)
                {
                    case "get":
                        result.Found = map.TryGetValue(request.Key!, out var value);
                        result.Value = value;
                        break;
                    case "set": vault.Set(request.Key!, request.Value!); break;
                    case "remove": vault.Remove(request.Key!); break;
                }
                return result;
            }, requireExisting: true);
            return JsonSerializer.SerializeToUtf8Bytes(response, Json);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or ArgumentException)
        { return Error("invalid-request"); }
        catch (CryptographicException) { return Error("vault-unavailable"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return Error("vault-unavailable"); }
    }

    private static byte[] Error(string code) => JsonSerializer.SerializeToUtf8Bytes(new Response { Error = code }, Json);

    internal static async Task ServeAsync(string name, string serviceSid, string ownerSid,
        SecretsStore vault, CancellationToken stop, Func<bool>? writesFrozen = null)
    {
        using var listener = BackupServicePipe.CreateListener(name, serviceSid, ownerSid);
        while (!stop.IsCancellationRequested)
        {
            await listener.WaitForConnectionAsync(stop).ConfigureAwait(false);
            byte[]? request = null, reply = null;
            try
            {
                request = await BackupServicePipe.ReadMessageAsync(listener, TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
                reply = Dispatch(request, vault, writesFrozen);
                await BackupServicePipe.SendResponseAsync(listener, reply, TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
            { /* Broken, malformed or silent clients do not terminate the service. Never log their payload. */ }
            finally
            {
                if (request is not null) CryptographicOperations.ZeroMemory(request);
                if (reply is not null) CryptographicOperations.ZeroMemory(reply);
                listener.Disconnect();
            }
        }
    }
}
