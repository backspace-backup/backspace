using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>Read-only run metadata for the authenticated desktop owner. Requests
/// identify a backup and a page, never a file or a destination for service writes.</summary>
internal sealed class ServiceHistoryProtocol(Func<string, List<RunRecord>> readHistory)
{
    internal sealed record Request(int Version, string BackupId, string? AfterRunId = null, int Limit = 30);
    internal sealed record Response(int Version, RunRecord[] Runs, string? NextAfterRunId = null, string? Error = null);
    internal const int MaximumResponseBytes = 768 * 1024;

    internal byte[] Dispatch(byte[] bytes)
    {
        try
        {
            var request = Parse(bytes);
            List<RunRecord> saved;
            try { saved = readHistory(request.BackupId); }
            catch (JsonException) { return Error("history-unavailable"); }
            if (saved.Any(r => r is null || !Guid.TryParseExact(r.Id, "N", out _) || r.Cells is null || r.Cells.Any(c => c is null)))
                return Error("history-unavailable");
            var records = saved.Where(r => r.BackupId == request.BackupId)
                .GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.Last())
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.Id, StringComparer.Ordinal).ToList();
            var offset = 0;
            if (request.AfterRunId is not null)
            {
                offset = records.FindIndex(r => r.Id == request.AfterRunId) + 1;
                if (offset == 0) return Error("history-changed");
            }
            var page = new List<RunRecord>();
            byte[] encoded = Encode(new(1, Array.Empty<RunRecord>()));
            for (var index = offset; index < records.Count && page.Count < request.Limit; index++)
            {
                // Return independent metadata, not paths the desktop cannot
                // open. The service never changes its original history here.
                var copy = JsonSerializer.Deserialize<RunRecord>(
                    JsonSerializer.SerializeToUtf8Bytes(records[index], ServiceRunAuthorization.Json), ServiceRunAuthorization.Json)!;
                copy.LogPath = "";
                foreach (var cell in copy.Cells) cell.LogPath = "";
                page.Add(copy);
                var candidate = Encode(new(1, page.ToArray(), index + 1 < records.Count ? copy.Id : null));
                if (candidate.Length > MaximumResponseBytes)
                {
                    // Never silently skip a result that is too large to send.
                    if (page.Count == 1) return Error("run-too-large");
                    break;
                }
                encoded = candidate;
            }
            return encoded;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        { return Error("invalid-history-request"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Error("history-unavailable"); }
    }

    internal static Request Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 4096) throw new InvalidDataException();
        var reader = new Utf8JsonReader(bytes);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.PropertyName && !properties.Add(reader.GetString()!))
                throw new JsonException("Duplicate property.");
        var request = JsonSerializer.Deserialize<Request>(bytes, ServiceRunAuthorization.Json) ?? throw new JsonException();
        if (request.Version != 1 || string.IsNullOrWhiteSpace(request.BackupId) || request.BackupId.Length > 256
            || request.Limit is < 1 or > 100
            || (request.AfterRunId is not null && !Guid.TryParseExact(request.AfterRunId, "N", out _)))
            throw new InvalidDataException();
        return request;
    }

    private static byte[] Encode(Response response) => JsonSerializer.SerializeToUtf8Bytes(response, ServiceRunAuthorization.Json);
    private static byte[] Error(string error) => Encode(new(1, Array.Empty<RunRecord>(), Error: error));

    internal async Task ServeAsync(string name, string serviceSid, string ownerSid, CancellationToken stop)
    {
        using var listener = BackupServicePipe.CreateListener(name, serviceSid, ownerSid);
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

    internal static async Task<Response> ReadAsync(ServiceWorkerBinding binding, Request request, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json);
        Parse(payload);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        byte[]? bytes = null;
        try
        {
            using var client = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-history", binding.ServiceSid,
                TimeSpan.FromSeconds(10), deadline.Token).ConfigureAwait(false);
            await BackupServicePipe.WriteMessageAsync(client, payload, TimeSpan.FromSeconds(10), deadline.Token).ConfigureAwait(false);
            bytes = await BackupServicePipe.ReadResponseAsync(client, TimeSpan.FromSeconds(10), deadline.Token).ConfigureAwait(false);
            if (bytes.Length > MaximumResponseBytes) throw new InvalidDataException("The service history response is too large.");
            var response = JsonSerializer.Deserialize<Response>(bytes, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
            if (response.Version != 1 || response.Runs is null || response.Runs.Length > request.Limit
                || response.Runs.Any(r => r is null || r.BackupId != request.BackupId || !Guid.TryParseExact(r.Id, "N", out _)
                    || r.LogPath != "" || r.Cells is null || r.Cells.Any(c => c is null || c.LogPath != ""))
                || (response.NextAfterRunId is not null && (response.Runs.Length == 0
                    || response.NextAfterRunId != response.Runs[^1].Id || response.NextAfterRunId == request.AfterRunId)))
                throw new InvalidDataException("The service returned inconsistent backup history.");
            return response;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
