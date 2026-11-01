using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Windows Job Object configured with KILL_ON_JOB_CLOSE. Every engine
/// child process (backrest.exe + the restic / rclone processes it
/// spawns) is assigned to this job; when Backspace exits or crashes,
/// Windows tears the job down and every member process with it.
/// Without this safety net, a backrest.exe whose parent died
/// ungracefully would keep running until it finished — accumulating
/// across crashes into dozens of orphans in Task Manager.
///
/// CancellationToken-based <c>process.Kill(entireProcessTree: true)</c>
/// covers the cooperative path; the job object is the safety net for
/// the catastrophic path. Both are kept because Kill is faster and
/// gets stderr drained cleanly; the job object catches anything Kill
/// missed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsKillOnExitJobObject
{
    private static readonly Lazy<IntPtr> _job = new(Create, isThreadSafe: true);

    /// <summary>
    /// Assigns <paramref name="process"/> to the per-process job object,
    /// guaranteeing it dies when Backspace exits. Best-effort: if the
    /// API isn't available (e.g. running under restricted permissions)
    /// the call is a silent no-op and the process simply isn't
    /// job-assigned.
    /// </summary>
    public static bool TryAssign(Process process)
    {
        try
        {
            if (process.HasExited) return false;
            // Persist provenance even when assignment fails: that is precisely
            // when a later crash could leave a child outside the Windows job.
            OwnedProcessRegistry.Register(process);
            var job = _job.Value;
            if (job == IntPtr.Zero)
            {
                AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(
                    "Job object isn't available; pid {Pid} ({Name}) won't be killed automatically if Backspace exits.",
                    SafePid(process), SafeName(process));
                return false;
            }
            // Process.Handle requires the caller to have already called
            // process.Start() — guaranteed by every site that calls us.
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                var err = Marshal.GetLastWin32Error();
                AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(
                    "AssignProcessToJobObject failed for pid {Pid} ({Name}) — Win32 error {Err}; this process may survive a parent crash.",
                    SafePid(process), SafeName(process), err);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // Restricted token, sandbox, or process gone before assign —
            // not fatal for the operation, but flag it so an operator
            // investigating "I keep seeing orphan engine processes" has
            // a trail. The cooperative cancel path (process.Kill in the
            // runner) still works.
            AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(ex,
                "TryAssign threw for pid {Pid} ({Name}); the process won't be killed automatically on a parent crash.",
                SafePid(process), SafeName(process));
            return false;
        }
    }

    private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }
    private static string SafeName(Process p) { try { return p.ProcessName; } catch { return "<unknown>"; } }

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(
                    "CreateJobObject returned NULL (Win32 error {Err}); engine child processes will not be killed on app crash.",
                    Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }

            // Configure the job so closing the LAST handle (i.e. when
            // Backspace process dies) terminates every member.
            // Do not enable SILENT_BREAKAWAY_OK: that flag lets Backrest's
            // restic/rclone children escape THIS job and survive Backspace.
            // Modern Windows supports nested jobs; assignment failures are
            // reported to callers instead of weakening descendant ownership.
            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            };
            var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = info,
            };
            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(ext, buf, fDeleteOld: false);
                if (!SetInformationJobObject(job,
                        JobObjectInfoType.ExtendedLimitInformation,
                        buf, (uint)len))
                {
                    AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(
                        "SetInformationJobObject failed (Win32 error {Err}); engine child processes will not be killed on app crash.",
                        Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return job;
        }
        catch (Exception ex)
        {
            // Restricted token, sandboxed environment, missing kernel32 —
            // log so an operator investigating "why are the engine binary
            // orphans accumulating after a crash" has a starting point.
            AppLogger.For(nameof(WindowsKillOnExitJobObject)).Warning(ex,
                "Job Object creation threw; engine child-process orphan-protection unavailable");
            return IntPtr.Zero;
        }
    }

    // ---- Win32 P/Invoke ------------------------------------------------

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE  = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
