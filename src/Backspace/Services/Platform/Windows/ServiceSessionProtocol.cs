using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>The service owns its pause file. The desktop sends bounded actions,
/// never paths. Windows identifies the pipe client's session; the service
/// ends the pause when Windows reports that session's sign-out, after a
/// restart, or once the session no longer exists.</summary>
internal sealed class ServiceSessionProtocol(Func<int, SessionTaskControl> controls,
    Func<int, InteractiveSessionIdentity, bool>? validateSession = null)
{
    internal sealed record Request(int Version, string Action, string? Generation = null, bool Force = false,
        InteractiveSessionIdentity? Session = null);
    internal sealed record Response(int Version, string? Generation = null, bool Running = false, string? Error = null,
        SessionTaskControl.PauseState? Pause = null);

    internal static Request Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 1024) throw new InvalidDataException("Invalid session request size.");
        var reader = new Utf8JsonReader(bytes);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.PropertyName && !names.Add(reader.GetString()!)) throw new JsonException();
        var request = JsonSerializer.Deserialize<Request>(bytes, ServiceRunAuthorization.Json) ?? throw new JsonException();
        if (request.Version != 1 || (request.Action switch
        {
            "pause" => request.Generation is not null || (request.Session is { } session
                && (session.SessionId <= 0 || session.LogonTime <= 0 || string.IsNullOrWhiteSpace(session.Account) || session.Account.Length > 256)),
            "resume" or "running" or "state" => request.Generation is not null || request.Force || request.Session is not null,
            "undo" => !Guid.TryParseExact(request.Generation, "N", out _) || request.Force || request.Session is not null,
            _ => true
        })) throw new InvalidDataException("Unsupported session request.");
        return request;
    }

    internal byte[] Dispatch(byte[] bytes, int clientSession)
    {
        try
        {
            var request = Parse(bytes);
            var control = controls(clientSession);
            var response = request.Action switch
            {
                "pause" => Pause(control, request, clientSession),
                "running" => new Response(1, Running: control.HasRunningTasks),
                "state" => new Response(1, Pause: control.ReadSavedPause()),
                _ => Change(control, request)
            };
            return JsonSerializer.SerializeToUtf8Bytes(response, ServiceRunAuthorization.Json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or SecurityException)
        {
            return JsonSerializer.SerializeToUtf8Bytes(new Response(1, Error: "session-action-failed"), ServiceRunAuthorization.Json);
        }
    }

    private Response Pause(SessionTaskControl control, Request request, int clientSession)
    {
        if (validateSession is null) return new(1, control.PauseForCurrentSession(request.Force)
            ?? throw new InvalidOperationException("No interactive session."));
        if (request.Session is not { } session || !validateSession(clientSession, session))
            throw new SecurityException("The pause does not belong to this Windows sign-in session.");
        return new(1, control.PauseForSession(session, request.Force));
    }

    internal static bool MatchesOwnerSession(int clientSession, InteractiveSessionIdentity claimed, string ownerSid)
        => clientSession > 0 && clientSession == claimed.SessionId
            && ((SecurityIdentifier)new NTAccount(claimed.Account).Translate(typeof(SecurityIdentifier))).Value == ownerSid;

    private static Response Change(SessionTaskControl control, Request request)
    {
        if (request.Action == "resume") control.ResumeOnGuiLaunch();
        else control.CancelQuit(request.Generation);
        return new(1);
    }

    internal async Task ServeAsync(ServiceWorkerBinding binding, CancellationToken stop)
    {
        using var listener = BackupServicePipe.CreateListener(binding.ServiceName + "-session", binding.ServiceSid, binding.OwnerSid);
        while (!stop.IsCancellationRequested)
        {
            await listener.WaitForConnectionAsync(stop).ConfigureAwait(false);
            byte[]? request = null, response = null;
            try
            {
                request = await BackupServicePipe.ReadMessageAsync(listener, TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
                if (!GetNamedPipeClientSessionId(listener.SafePipeHandle, out var session))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                response = Dispatch(request, checked((int)session));
                await BackupServicePipe.SendResponseAsync(listener, response, TimeSpan.FromSeconds(10), stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or Win32Exception) { }
            finally
            {
                if (request is not null) CryptographicOperations.ZeroMemory(request);
                if (response is not null) CryptographicOperations.ZeroMemory(response);
                listener.Disconnect();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
}

internal sealed class ServiceSessionClient(ServiceWorkerBinding binding)
{
    internal static ServiceSessionClient? ForDesktop()
    {
        var root = AppContext.BaseDirectory.TrimEnd('\\');
        if (!ServiceOwnerSecrets.Applies(root, AppPaths.ConfigRoot)) return null;
        var context = ServiceCoordinationFiles.Resolve(root, AppPaths.ConfigRoot);
        return context is null ? null : new(context.Binding);
    }

    internal string Pause(bool force, CancellationToken ct) => Send(new(1, "pause", Force: force,
        Session: new WindowsInteractiveSessionReader(binding.OwnerSid).Current()
            ?? throw new InvalidOperationException("Windows could not identify your sign-in session. Backspace has not quit.")), ct).Generation
        ?? throw new IOException("The service did not confirm the task pause. Backspace has not quit.");
    internal void Resume(CancellationToken ct) => Send(new(1, "resume"), ct);
    internal void CancelQuit(string generation, CancellationToken ct) => Send(new(1, "undo", generation), ct);
    internal bool HasRunningTasks() => Send(new(1, "running"), CancellationToken.None).Running;
    internal SessionTaskControl.PauseState? SavedPause() => Send(new(1, "state"), CancellationToken.None).Pause;

    private ServiceSessionProtocol.Response Send(ServiceSessionProtocol.Request request, CancellationToken ct)
        => SendAsync(request, ct).GetAwaiter().GetResult();

    internal async Task<ServiceSessionProtocol.Response> SendAsync(ServiceSessionProtocol.Request request, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json);
        ServiceSessionProtocol.Parse(bytes);
        ct.ThrowIfCancellationRequested();
        using var controller = new ServiceController(binding.ServiceName);
        ServiceRegistration.EnsureRunning(controller, TimeSpan.FromSeconds(20));
        byte[]? reply = null;
        try
        {
            using var pipe = await BackupServicePipe.ConnectAsync(binding.ServiceName + "-session", binding.ServiceSid,
                TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            await BackupServicePipe.WriteMessageAsync(pipe, bytes, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            reply = await BackupServicePipe.ReadResponseAsync(pipe, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (reply.Length > 1024) throw new InvalidDataException("Invalid task-pause response size.");
            var result = JsonSerializer.Deserialize<ServiceSessionProtocol.Response>(reply, ServiceRunAuthorization.Json)
                ?? throw new InvalidDataException("The service did not confirm the task-pause action.");
            if (result.Version != 1 || result.Error is not null || (request.Action == "pause"
                ? !Guid.TryParseExact(result.Generation, "N", out _) : result.Generation is not null)
                || (request.Action == "state" ? result.Pause is null : result.Pause is not null))
                throw new IOException("The background service could not confirm the task-pause action. Please try again.");
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (reply is not null) CryptographicOperations.ZeroMemory(reply);
        }
    }
}
