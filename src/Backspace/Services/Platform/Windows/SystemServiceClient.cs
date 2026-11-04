using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>The owner's requests to the version-3 service that are not runs
/// (U100): preparing to take the saved sign-ins back, or cancelling that.</summary>
internal static class SystemServiceClient
{
    internal sealed record FreezeReply(int Version, string? ReturnId = null, string? State = null, string? Error = null);

    internal static string Freeze(SystemService.Registration system)
    {
        var reply = JsonSerializer.Deserialize<FreezeReply>(Send(system, "freeze"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("The background service did not answer.");
        if (reply.Error == "busy") throw new IOException("A backup is running in the background. Turn administrator access off after it finishes.");
        if (reply.Error is not null || !Guid.TryParseExact(reply.ReturnId, "N", out _))
            throw new IOException("The background service could not prepare to return your saved sign-ins. Nothing was changed.");
        return reply.ReturnId!;
    }

    internal static void Thaw(SystemService.Registration system) => Send(system, "thaw");

    /// <summary>After removal the service's copy of the sign-ins is useless
    /// (only that service could read it); its history stays.</summary>
    internal static void ForgetVault(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory)) return;
        foreach (var pattern in new[] { "secrets.bin*", "migration-*", "return-*", SystemServiceLifetime.FileName })
            foreach (var file in Directory.EnumerateFiles(dataDirectory, pattern))
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The size of a folder measured by the service, with the same
    /// rights as its backups (U106), or null when it can't tell.</summary>
    internal static async Task<SourceSizeResult?> MeasureAsync(SystemService.Registration system, string path, SourceSizeRules rules,
        TimeSpan limit, CancellationToken ct)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        object request = rules.HasRules || rules.SkipCloudOnly
            ? new { version = 1, command = "measure", path, filters = rules.FiltersText, skipCloudOnly = rules.SkipCloudOnly }
            : new { version = 1, command = "measure", path };
        var started = JsonSerializer.Deserialize<ServiceSizeMeasurements.Reply>(
            await SendAsync(system, request, ct).ConfigureAwait(false), options);
        if (started?.RunId is not { } runId || started.Error is not null) return null;
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize<ServiceSizeMeasurements.Reply>(
                await SendAsync(system, new { version = 1, command = "measure-result", runId }, ct).ConfigureAwait(false), options);
            if (reply is null || reply.Error is not null) return null;
            if (reply.State == "done")
                return new SourceSizeResult(path, reply.Bytes, reply.Files, Completed: true, Truncated: reply.Truncated,
                    UnreadableFolders: reply.Unreadable, MeasuredByService: true, CloudOnlyFiles: reply.CloudOnly);
        }
        return null;
    }

    private static byte[] Send(SystemService.Registration system, string command)
        => SendAsync(system, new { version = 1, command }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task<byte[]> SendAsync(SystemService.Registration system, object request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var pipe = await BackupServicePipe.ConnectAsync(system.ServiceName + "-runs", SystemService.LocalSystemSid, TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request), TimeSpan.FromSeconds(10), timeout.Token).ConfigureAwait(false);
        return await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(10), timeout.Token).ConfigureAwait(false);
    }
}
