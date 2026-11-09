using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.Platform.Windows;

/// <summary>The owner's requests to the version-3 service that are not runs
/// (U100): size estimates with the service's rights.</summary>
internal static class SystemServiceClient
{
    /// <summary>After removal the service's Quit record is of no further use;
    /// its history stays.</summary>
    internal static void ForgetQuitRecord(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, SystemServiceLifetime.FileName);
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    private static async Task<byte[]> SendAsync(SystemService.Registration system, object request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var pipe = await BackupServicePipe.ConnectAsync(system.ServiceName + "-runs", SystemService.LocalSystemSid, TimeSpan.FromSeconds(15), timeout.Token).ConfigureAwait(false);
        await BackupServicePipe.WriteMessageAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request), TimeSpan.FromSeconds(10), timeout.Token).ConfigureAwait(false);
        return await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(10), timeout.Token).ConfigureAwait(false);
    }
}
