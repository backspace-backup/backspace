using System.Diagnostics;
using Backspace.Services.Platform;

namespace Backspace.Services;

/// <summary>
/// Process-orphan safety net. On Windows: a Job Object configured with
/// <c>KILL_ON_JOB_CLOSE</c> — every spawned restic process is
/// assigned to it; when Backspace exits (or crashes), Windows tears
/// down the job and every member with it. Without this, a restic
/// whose parent died ungracefully would keep running until it finished.
///
/// <para>
/// On macOS / Linux: no equivalent kernel facility, so this is a
/// runtime no-op. The cooperative cancel path
/// (<c>process.Kill(entireProcessTree: true)</c> wired up at every
/// spawn site) covers the graceful exit; orphans on a kill -9 of the
/// parent do leak until restic finishes naturally. Acceptable
/// trade-off for v1; a follow-up could use
/// <c>setsid</c> + <c>kill(-pgid, SIGTERM)</c> on Unix to mimic the
/// job-object semantics.
/// </para>
/// </summary>
internal static class KillOnExitJobObject
{
    /// <summary>
    /// Assigns <paramref name="process"/> to the per-process Windows
    /// Job Object so it dies when Backspace exits. No-op on
    /// non-Windows platforms.
    /// </summary>
    public static bool TryAssign(Process process)
    {
        if (!PlatformInfo.IsWindows) return true;
#if WINDOWS
        return Platform.Windows.WindowsKillOnExitJobObject.TryAssign(process);
#else
        return true;
#endif
    }
}
