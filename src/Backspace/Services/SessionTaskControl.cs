using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services;

/// <summary>Explicit Quit pauses this account's tasks for its current Windows
/// sign-in session. The scheduler remains enabled for sign-out and reboot.
/// The state lock orders task admission against Quit; the shared run handle
/// stays held until the scheduled process has stopped its engine.</summary>
public sealed class SessionTaskControl
{
    private readonly IInteractiveSessionReader _sessions;
    private readonly string _statePath;
    private readonly string _runsPath;
    private readonly bool _resolveService;
    private readonly bool _externallyCheckedSession;
#if WINDOWS
    private bool _usedService;
    private Platform.Windows.ServiceSessionClient? Service()
    {
        if (!_resolveService) return null;
        var client = Platform.Windows.ServiceSessionClient.ForDesktop();
        if (client is not null) _usedService = true;
        else if (_usedService) throw new IOException("The background service registration is missing. The local task-pause record was not used.");
        return client;
    }
#endif
    public const string PausedMessage = "Backspace was quit for this Windows sign-in session. Open Backspace to resume tasks, or sign out of Windows.";

    public SessionTaskControl() : this(AppPaths.ConfigRoot, CreateSessionReader()) { _resolveService = true; }
    internal SessionTaskControl(string root, IInteractiveSessionReader sessions, bool externallyCheckedSession = false)
    {
        _sessions = sessions;
        _externallyCheckedSession = externallyCheckedSession;
        var account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessions.AccountId)))[..24];
        _statePath = Path.Combine(root, $"task-pause-{account}.json");
        _runsPath = Path.Combine(root, $"scheduled-runs-{account}.lock");
    }

    private static IInteractiveSessionReader CreateSessionReader()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new Platform.Windows.WindowsInteractiveSessionReader();
#endif
        return new UnsupportedSessionReader();
    }

    /// <summary><paramref name="BootId"/> identifies the Windows start the
    /// pause was made in, so a restart ends it (null in earlier records).</summary>
    internal sealed record PauseState(string Generation, InteractiveSessionIdentity? Session, bool Force, uint? BootId = null);
    private PauseState Read() => !File.Exists(_statePath) ? new("", null, false)
        : JsonSerializer.Deserialize<PauseState>(File.ReadAllText(_statePath))
            ?? throw new InvalidDataException("The saved task-pause state is unreadable.");

    private void Write(PauseState state)
    {
        var staging = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, _statePath, overwrite: true);
        }
        finally { try { if (File.Exists(staging)) File.Delete(staging); } catch { /* Preserve the original error. */ } }
    }

    private bool IsPaused(PauseState state) => state.Session is { } saved
        && (_externallyCheckedSession || _sessions.Read(saved.SessionId) == saved);

    internal PauseState ReadSavedPause()
    {
        using var transaction = StateFileLock.Acquire(_statePath);
        return Read();
    }

    public string? PauseForCurrentSession(bool force = false, CancellationToken ct = default)
    {
#if WINDOWS
        if (Service() is { } service) return service.Pause(force, ct);
#endif
        if (!_sessions.Supported) return null;
        var session = _sessions.Current()
            ?? throw new InvalidOperationException("Windows could not identify your sign-in session. Backspace cannot safely save the task pause.");
        return PauseForSession(session, force, ct);
    }

    internal string PauseForSession(InteractiveSessionIdentity session, bool force, CancellationToken ct = default)
    {
        var generation = Guid.NewGuid().ToString("N");
        FileAccessRetry.Run(() =>
        {
            // Release the transaction between attempts: a waiting GUI must
            // not block a scheduled process from checking its current state.
            using var transaction = StateFileLock.Acquire(_statePath, TimeSpan.Zero);
            _ = Read(); // Never replace an unreadable existing record silently.
            Write(new(generation, session, force, _sessions.BootId()));
        }, _statePath, FileWaitReason.Quit, ct);
        return generation;
    }

    /// <summary>Called on an actual GUI launch, not by a scheduled process or
    /// engine helper. The generation is retained so a quick reopen cannot hide
    /// Quit from a task that was already running.</summary>
    public void ResumeOnGuiLaunch(CancellationToken ct = default)
    {
#if WINDOWS
        if (Service() is { } service) { service.Resume(ct); return; }
#endif
        if (!_sessions.Supported) return;
        FileAccessRetry.Run(() =>
        {
            using var transaction = StateFileLock.Acquire(_statePath, TimeSpan.Zero);
            // Quit can cancel a startup resume while it waits for this lock.
            // Recheck inside the transaction so a late resume cannot undo Quit.
            ct.ThrowIfCancellationRequested();
            var state = Read();
            if (state.Session is not null) Write(state with { Session = null });
        }, _statePath, FileWaitReason.Resume, ct);
    }

    public void CancelQuit(string? generation, CancellationToken ct = default)
    {
#if WINDOWS
        if (Service() is { } service) { if (generation is not null) service.CancelQuit(generation, ct); return; }
#endif
        if (!_sessions.Supported || generation is null) return;
        FileAccessRetry.Run(() =>
        {
            using var transaction = StateFileLock.Acquire(_statePath, TimeSpan.Zero);
            var state = Read();
            if (state.Generation == generation && state.Session is not null)
                Write(state with { Session = null });
        }, _statePath, FileWaitReason.UndoQuit, ct);
    }

    /// <summary>The background service ends a Quit pause once its sign-in
    /// session has ended. The generation is kept, as for a resume.</summary>
    internal bool EndPauseIf(Func<PauseState, bool> ended)
    {
        using var transaction = StateFileLock.Acquire(_statePath);
        var state = Read();
        if (state.Session is null || !ended(state)) return false;
        Write(state with { Session = null });
        return true;
    }

    /// <summary>True while Quit paused tasks for the current sign-in session.
    /// A start at sign-in then stays closed.</summary>
    public bool CurrentSessionPaused()
    {
#if WINDOWS
        if (Service() is { } service)
            return service.SavedPause() is { Session: { } saved } && saved == _sessions.Current();
#endif
        if (!_sessions.Supported) return false;
        PauseState state;
        using (StateFileLock.Acquire(_statePath)) state = Read();
        return state.Session is { } paused && paused == _sessions.Current();
    }

    public bool HasRunningTasks
    {
        get
        {
#if WINDOWS
            if (Service() is { } service) return service.HasRunningTasks();
#endif
            if (!_sessions.Supported || !Directory.Exists(Path.GetDirectoryName(_runsPath))) return false;
            // Serialize this momentary exclusive probe with new task admission;
            // otherwise a task could mistake the probe for a real file conflict.
            using var transaction = StateFileLock.Acquire(_statePath);
            if (!File.Exists(_runsPath)) return false;
            try
            {
                using var exclusive = new FileStream(_runsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (IOException) { return true; }
        }
    }

    public bool TryBeginUnattended(out UnattendedLease? lease, Func<Task>? forceStop = null)
    {
        lease = null;
#if WINDOWS
        if (Service() is not null)
            throw new InvalidOperationException("Background backup is managed by the Windows service. Use its approved schedule to start unattended work.");
#endif
        if (!_sessions.Supported) return true;
        using var transaction = StateFileLock.Acquire(_statePath);
        var state = Read();
        if (IsPaused(state)) return false;
        if (!File.Exists(_runsPath))
        {
            using var create = new FileStream(_runsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        var handle = new FileStream(_runsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        lease = new UnattendedLease(this, state.Generation, handle, forceStop);
        return true;
    }

    public sealed class UnattendedLease : IDisposable
    {
        private readonly FileStream _handle;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _work = new();
        private readonly Task _monitor;
        private int _disposed;
        public CancellationToken Token => _work.Token;

        internal UnattendedLease(SessionTaskControl owner, string generation, FileStream handle, Func<Task>? forceStop)
        {
            _handle = handle;
            // Only a running scheduled process polls. There is no idle tray timer.
            _monitor = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(250, _stop.Token).ConfigureAwait(false);
                        PauseState current;
                        using (StateFileLock.Acquire(owner._statePath)) current = owner.Read();
                        if (current.Generation == generation) continue;
                        _work.Cancel();
                        if (current.Force && forceStop is not null) await forceStop().ConfigureAwait(false);
                        generation = current.Generation;
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    AppLogger.For<SessionTaskControl>().Warning(ex, "Stopping scheduled work because its session-pause state could not be checked");
                    _work.Cancel();
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { _monitor.GetAwaiter().GetResult(); }
            finally { _handle.Dispose(); _work.Dispose(); _stop.Dispose(); }
        }
    }

    private sealed class UnsupportedSessionReader : IInteractiveSessionReader
    {
        public bool Supported => false;
        public string AccountId => "unsupported";
        public InteractiveSessionIdentity? Current() => null;
        public InteractiveSessionIdentity? Read(int sessionId) => null;
    }
}

internal sealed record InteractiveSessionIdentity(int SessionId, long LogonTime, string Account);
internal interface IInteractiveSessionReader
{
    /// <summary>Changes with every Windows start; null where unknown.</summary>
    uint? BootId() => null;
    bool Supported { get; }
    string AccountId { get; }
    InteractiveSessionIdentity? Current();
    InteractiveSessionIdentity? Read(int sessionId);
}
