using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>The owner requests a recorded run, never a privileged file path.
/// Return a bounded tail of its existing transcript without writing desktop files.</summary>
internal sealed class ServiceTranscriptProtocol(string logsRoot, Func<string, List<RunRecord>> readHistory)
{
    internal const int TailBytes = 64 * 1024;
    internal const int MaximumResponseBytes = 512 * 1024;
    internal sealed record Request(int Version, string BackupId, string RunId);
    internal sealed record Response(int Version, string RunId, string Text, bool Truncated = false, string? Error = null);

    internal byte[] Dispatch(byte[] bytes)
    {
        Request? request = null;
        try
        {
            request = Parse(bytes);
            var run = readHistory(request.BackupId).LastOrDefault(r => r.BackupId == request.BackupId && r.Id == request.RunId);
            if (run is null) return Encode(new(1, request.RunId, "", Error: "run-not-found"));
            if (string.IsNullOrWhiteSpace(run.LogPath)) return Encode(new(1, request.RunId, "", Error: "transcript-unavailable"));
            var root = Path.GetFullPath(logsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(run.LogPath);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(path), ".log", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            // The service controls this tree. Reject a redirected component
            // rather than using the worker's backup privileges through it.
            for (var current = path; current.Length >= root.Length - 1; current = Path.GetDirectoryName(current)!)
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var truncated = file.Length > TailBytes;
            if (truncated) file.Position = file.Length - TailBytes;
            var buffer = new byte[(int)Math.Min(TailBytes, file.Length)];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = file.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            var start = 0;
            if (truncated) while (start < Math.Min(3, count) && (buffer[start] & 0xC0) == 0x80) start++;
            var text = Encoding.UTF8.GetString(buffer, start, count - start);
            CryptographicOperations.ZeroMemory(buffer);
            return Encode(new(1, request.RunId, text, truncated));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Encode(new(1, request?.RunId ?? "", "", Error: "transcript-unavailable")); }
    }

    internal static Request Parse(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > 4096) throw new InvalidDataException();
        ServiceRunAuthorization.RejectDuplicateProperties(bytes);
        var request = JsonSerializer.Deserialize<Request>(bytes, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
        if (request.Version != 1 || string.IsNullOrWhiteSpace(request.BackupId) || request.BackupId.Length > 256
            || !Guid.TryParseExact(request.RunId, "N", out _)) throw new InvalidDataException();
        return request;
    }
    private static byte[] Encode(Response response) => JsonSerializer.SerializeToUtf8Bytes(response, ServiceRunAuthorization.Json);

    internal async Task ServeAsync(ServiceWorkerBinding binding, CancellationToken stop)
    {
        using var listener = BackupServicePipe.CreateListener(binding.ServiceName + "-transcript", binding.ServiceSid, binding.OwnerSid);
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

    internal static async Task<Response> ReadAsync(ServiceWorkerBinding binding, Request request, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json); Parse(payload);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-transcript", binding.ServiceSid, TimeSpan.FromSeconds(10), deadline.Token).ConfigureAwait(false);
        byte[]? bytes = null;
        try
        {
            await BackupServicePipe.WriteMessageAsync(pipe, payload, TimeSpan.FromSeconds(10), deadline.Token).ConfigureAwait(false);
            bytes = await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(10), deadline.Token).ConfigureAwait(false);
            if (bytes.Length > MaximumResponseBytes) throw new InvalidDataException();
            var response = JsonSerializer.Deserialize<Response>(bytes, ServiceRunAuthorization.Json) ?? throw new InvalidDataException();
            if (response.Version != 1 || response.RunId != request.RunId || response.Text is null || response.Text.Length > TailBytes)
                throw new InvalidDataException("The background service returned an inconsistent transcript.");
            return response;
        }
        finally { CryptographicOperations.ZeroMemory(payload); if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static async Task<string?> ReadForDesktopAsync(RunRecord run, CancellationToken token)
    {
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        if (!ServiceOwnerSecrets.Applies(root, AppPaths.ConfigRoot) || ServiceRetirement.Read(root) is not null
            || !File.Exists(ServicePortableLayout.Record(root, ServiceWorkerBinding.FileName))) return null;
        var binding = ServiceWorkerBinding.LoadForOwner(root);
        if (!binding.CanRun) return null;
        using var service = new System.ServiceProcess.ServiceController(binding.ServiceName);
        if (service.Status == System.ServiceProcess.ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        var result = await ReadAsync(binding, new(1, run.BackupId, run.Id), token).ConfigureAwait(false);
        return result.Error is not null ? "The detailed log for this background run is no longer available. Its saved result is still shown."
            : (result.Truncated ? "[Showing the latest 64 KB of this background run's log.]\n\n" : "") + result.Text;
    }
}
