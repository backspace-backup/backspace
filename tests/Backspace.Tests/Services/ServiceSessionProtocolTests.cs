using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using Backspace.Services;
using Backspace.Services.Platform.Windows;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ServiceSessionProtocolTests
{
    private sealed class Sessions : IInteractiveSessionReader
    {
        public bool Supported => true;
        public string AccountId => "service-owner";
        public InteractiveSessionIdentity? Session = new(7, 12345, "TEST\\OWNER");
        public int ClientSession = 7;
        public uint? Boot = 4;
        public uint? BootId() => Boot;
        public InteractiveSessionIdentity? Current() => Read(ClientSession);
        public InteractiveSessionIdentity? Read(int id) => Session?.SessionId == id ? Session : null;
    }

    private static ServiceSessionProtocol.Response Send(ServiceSessionProtocol server, string action,
        string? generation = null, bool force = false, int session = 7) =>
        JsonSerializer.Deserialize<ServiceSessionProtocol.Response>(server.Dispatch(
            JsonSerializer.SerializeToUtf8Bytes(new ServiceSessionProtocol.Request(1, action, generation, force), ServiceRunAuthorization.Json), session),
            ServiceRunAuthorization.Json)!;

    [Fact]
    public async Task Quit_cancels_running_work_and_quick_reopen_cannot_hide_it()
    {
        using var ws = new TempWorkspace();
        var sessions = new Sessions();
        var control = new SessionTaskControl(ws.ConfigRoot, sessions);
        var server = new ServiceSessionProtocol(id => { sessions.ClientSession = id; return control; });
        Assert.True(control.TryBeginUnattended(out var lease));
        using (lease)
        {
            Assert.True(Send(server, "running").Running);
            var paused = Send(server, "pause");
            Assert.Null(paused.Error);
            Assert.True(Guid.TryParseExact(paused.Generation, "N", out _));
            Assert.False(control.TryBeginUnattended(out _));
            Assert.Null(Send(server, "resume").Error);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(5000, lease!.Token));
            Assert.True(Send(server, "running").Running);
            Assert.True(control.TryBeginUnattended(out var next)); next!.Dispose();
        }
        Assert.False(Send(server, "running").Running);
    }

    [Fact]
    public async Task Force_after_normal_Quit_reaches_the_same_running_lease()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        var server = new ServiceSessionProtocol(_ => control);
        var forced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(control.TryBeginUnattended(out var lease, () => { forced.TrySetResult(); return Task.CompletedTask; }));
        using (lease)
        {
            Assert.Null(Send(server, "pause").Error);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(5000, lease!.Token));
            Assert.False(forced.Task.IsCompleted);
            Assert.Null(Send(server, "pause", force: true).Error);
            await forced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restart_retains_pause_but_signout_or_session_reuse_expires_it(bool reuse)
    {
        using var ws = new TempWorkspace();
        var sessions = new Sessions();
        Assert.Null(Send(new(_ => new(ws.ConfigRoot, sessions)), "pause").Error);
        var restarted = new SessionTaskControl(ws.ConfigRoot, sessions);
        Assert.False(restarted.TryBeginUnattended(out _));
        sessions.Session = reuse ? sessions.Session! with { LogonTime = 12346 } : null;
        Assert.True(restarted.TryBeginUnattended(out var lease)); lease!.Dispose();
    }

    [Fact]
    public void Client_without_owners_interactive_session_cannot_save_pause()
    {
        using var ws = new TempWorkspace();
        var sessions = new Sessions { ClientSession = 0 };
        var server = new ServiceSessionProtocol(_ => new(ws.ConfigRoot, sessions));
        Assert.Equal("session-action-failed", Send(server, "pause", session: 0).Error);
        Assert.Empty(Directory.GetFiles(ws.ConfigRoot, "task-pause-*.json"));
    }

    [Fact]
    public void Old_undo_and_malformed_requests_do_not_replace_current_pause()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        var server = new ServiceSessionProtocol(_ => control);
        var first = Send(server, "pause").Generation;
        Send(server, "pause");
        Assert.Null(Send(server, "undo", first).Error);
        Assert.False(control.TryBeginUnattended(out _));
        var path = Directory.GetFiles(ws.ConfigRoot, "task-pause-*.json").Single();
        var before = File.ReadAllText(path);
        foreach (var payload in new[]
        {
            "{\"version\":1,\"action\":\"resume\",\"sessionId\":7}",
            "{\"version\":1,\"action\":\"resume\",\"path\":\"C:\\\\other\"}",
            "{\"version\":1,\"action\":\"resume\",\"action\":\"pause\"}",
            "{\"version\":1,\"action\":\"undo\",\"generation\":\"wrong\"}",
            "{\"version\":2,\"action\":\"resume\"}",
            "{\"version\":1,\"action\":\"resume\",\"force\":true}",
            new string('x', 1025)
        })
        {
            var reply = JsonSerializer.Deserialize<ServiceSessionProtocol.Response>(server.Dispatch(Encoding.UTF8.GetBytes(payload), 7), ServiceRunAuthorization.Json)!;
            Assert.NotNull(reply.Error);
            Assert.Equal(before, File.ReadAllText(path));
        }
    }

    [Fact]
    public void Damaged_saved_state_is_not_replaced_by_a_resume_request()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions());
        var server = new ServiceSessionProtocol(_ => control);
        Send(server, "pause");
        var path = Directory.GetFiles(ws.ConfigRoot, "task-pause-*.json").Single();
        File.WriteAllText(path, "broken state");
        Assert.NotNull(Send(server, "resume").Error);
        Assert.Equal("broken state", File.ReadAllText(path));
        Assert.Throws<JsonException>(() => control.TryBeginUnattended(out _));
    }

    [Theory]
    [InlineData("same-session", false)]
    [InlineData("signed-out", true)]
    [InlineData("other-session-signed-out", false)]
    [InlineData("restarted", true)]
    public void Service_ends_the_pause_only_when_its_session_has_ended(string state, bool resumes)
    {
        using var ws = new TempWorkspace();
        var sessions = new Sessions();
        var original = sessions.Session!;
        var control = new SessionTaskControl(ws.ConfigRoot, sessions, externallyCheckedSession: true);
        var server = new ServiceSessionProtocol(_ => control, (id, claimed) => id == 7 && claimed == original);
        var request = new ServiceSessionProtocol.Request(1, "pause", Session: original);
        var reply = server.Dispatch(JsonSerializer.SerializeToUtf8Bytes(request, ServiceRunAuthorization.Json), 7);
        Assert.Null(JsonSerializer.Deserialize<ServiceSessionProtocol.Response>(reply, ServiceRunAuthorization.Json)!.Error);
        var generation = control.ReadSavedPause().Generation;
        // Windows reports a sign-out with the session's ID (OnSessionChange).
        var ended = state switch { "signed-out" => original.SessionId, "other-session-signed-out" => original.SessionId + 1, _ => -1 };
        Assert.Equal(4u, control.ReadSavedPause().BootId);
        if (state == "restarted") sessions.Boot = 5;
        Assert.Equal(resumes, control.EndPauseIf(pause => pause.Session!.SessionId == ended || pause.BootId != sessions.Boot));
        Assert.Equal(resumes, control.TryBeginUnattended(out var lease));
        lease?.Dispose();
        // A running task compares generations: ending the pause keeps it.
        Assert.Equal(generation, control.ReadSavedPause().Generation);
    }

    [Fact]
    public void Claimed_session_must_match_the_authenticated_pipe_client()
    {
        using var ws = new TempWorkspace();
        var control = new SessionTaskControl(ws.ConfigRoot, new Sessions(), externallyCheckedSession: true);
        var server = new ServiceSessionProtocol(_ => control, (id, claimed) => id == 7 && claimed.SessionId == id);
        var request = JsonSerializer.SerializeToUtf8Bytes(new ServiceSessionProtocol.Request(1, "pause",
            Session: new(7, 12345, "TEST\\OWNER")), ServiceRunAuthorization.Json);
        Assert.NotNull(JsonSerializer.Deserialize<ServiceSessionProtocol.Response>(server.Dispatch(request, 0), ServiceRunAuthorization.Json)!.Error);
        Assert.True(control.TryBeginUnattended(out var lease)); lease!.Dispose();
        Assert.Null(JsonSerializer.Deserialize<ServiceSessionProtocol.Response>(server.Dispatch(request, 7), ServiceRunAuthorization.Json)!.Error);
        Assert.False(control.TryBeginUnattended(out _));
    }
}
