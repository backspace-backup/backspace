using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

internal sealed class ServiceNotificationProtocol(ServiceNotificationQueue queue)
{
    internal sealed record Request(int Version, string Action, string? Id = null, string? Receipt = null);
    internal sealed record Response(int Version, ServiceNotificationQueue.Claim? Claim = null, bool Accepted = false, string? Error = null);
    internal static Request Parse(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > 1024) throw new InvalidDataException();
        ServiceRunAuthorization.RejectDuplicateProperties(bytes);
        var request = JsonSerializer.Deserialize<Request>(bytes, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
        if (request.Version != 1 || (request.Action switch
        {
            "take" => request.Id is not null || request.Receipt is not null,
            "ack" => !Guid.TryParseExact(request.Id, "N", out _) || !Guid.TryParseExact(request.Receipt, "N", out _),
            _ => true
        })) throw new InvalidDataException();
        return request;
    }

    internal byte[] Dispatch(byte[] bytes)
    {
        Response response;
        try
        {
            var request = Parse(bytes);
            response = request.Action == "take" ? new(1, queue.Take()) : new(1, Accepted: queue.Acknowledge(request.Id!, request.Receipt!));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { response = new(1, Error: "notification-unavailable"); }
        return JsonSerializer.SerializeToUtf8Bytes(response, ServiceRunAuthorization.Json);
    }

    internal async Task ServeAsync(ServiceWorkerBinding binding, CancellationToken stop)
    {
        using var listener = BackupServicePipe.CreateListener(binding.ServiceName + "-notifications", binding.ServiceSid, binding.OwnerSid);
        while (!stop.IsCancellationRequested)
        {
            await listener.WaitForConnectionAsync(stop).ConfigureAwait(false);
            try
            {
                var bytes = await BackupServicePipe.ReadMessageAsync(listener, TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
                await BackupServicePipe.SendResponseAsync(listener, Dispatch(bytes), TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { }
            finally { listener.Disconnect(); }
        }
    }

    internal static async Task<Response> SendAsync(ServiceWorkerBinding binding, Request request, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json); Parse(bytes);
        using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-notifications", binding.ServiceSid, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, bytes, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        var reply = await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (reply.Length > 128 * 1024) throw new InvalidDataException();
        var response = JsonSerializer.Deserialize<Response>(reply, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
        if (response.Version != 1 || response.Error is not null) throw new IOException("Background notifications could not be read; pending alerts were retained.");
        if (response.Claim is { } claim)
        {
            ServiceNotificationQueue.Validate(claim.Notice);
            if (!Guid.TryParseExact(claim.Receipt, "N", out _)) throw new InvalidDataException();
        }
        return response;
    }
}
