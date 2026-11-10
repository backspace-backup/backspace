using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Asks a console program to stop the way Ctrl+Break in its window would
/// (U175). restic hears it as an interrupt: it stops what it is doing,
/// removes its own repository lock and ends with exit code 130. Killing it
/// instead leaves the lock behind, which the next exclusive command (cleanup,
/// check) then has to clear with <c>restic unlock</c>.
///
/// <para>Windows delivers the event only to processes that share the
/// sender's console, and only to one process group: restic is started in a
/// group of its own (<c>CreateNewProcessGroup</c>), so the event reaches
/// restic and nothing else. The rclone restic starts for a cloud destination
/// is in a group of its own too (restic starts it so), so it keeps carrying
/// restic's last requests, such as deleting the lock, until restic closes it.
/// A program without a console (the app, the background service's workers)
/// attaches to restic's own hidden console for the moment of sending; a
/// program that has one (a test host, a developer's terminal) shares it with
/// restic from the start, since attaching elsewhere would mean leaving its
/// own. Attaching is per process, so every start and every signal takes
/// <see cref="Gate"/>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleBreak
{
    /// <summary>Held while a restic process is started (to decide whether it
    /// shares this process's console) and while a signal is sent.</summary>
    internal static readonly object Gate = new();

    /// <summary>True when this process is attached to a console.</summary>
    internal static bool HasConsole()
    {
        var ids = new uint[1];
        return GetConsoleProcessList(ids, 1) > 0;
    }

    /// <summary>Sends Ctrl+Break to the process group <paramref name="groupId"/>
    /// (the id of a process started with <c>CreateNewProcessGroup</c>).
    /// <paramref name="sharedConsole"/>: it was started on this process's
    /// console. False when the event could not be sent.</summary>
    internal static bool Send(int groupId, bool sharedConsole)
    {
        lock (Gate)
        {
            // Should Windows ever deliver it here too (a process not in a group
            // of its own gets every process on the console signalled), this
            // process ignores it rather than end.
            if (_guard is null)
            {
                _guard = type => type == CtrlBreakEvent;
                SetConsoleCtrlHandler(_guard, add: true);
            }
            if (sharedConsole) return GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)groupId);
            // Attaching needs this process to have no console of its own.
            if (HasConsole() || !AttachConsole((uint)groupId)) return false;
            try { return GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)groupId); }
            finally { FreeConsole(); }
        }
    }

    private const uint CtrlBreakEvent = 1;

    private delegate bool HandlerRoutine(uint ctrlType);
    // Kept for the life of the process: Windows calls it from its own thread.
    private static HandlerRoutine? _guard;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
