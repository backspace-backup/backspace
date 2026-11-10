using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// One restic run's own Windows job (U175): restic and everything it starts,
/// such as the rclone that carries its data to a cloud destination. Ending
/// the job ends all of them at once; closing it (disposing this object, or
/// Backspace ending) ends whatever is still running in it. A plain process
/// kill ends restic alone and leaves its rclone running.
///
/// <para>The job nests inside Backspace's own kill-on-close job
/// (<see cref="WindowsKillOnExitJobObject"/>): the process must be assigned to
/// that one first, then to this empty job, which Windows then places inside
/// it. The other order would put the shared job inside this one.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessTreeJob : IDisposable
{
    private readonly JobHandle _job;

    private ProcessTreeJob(JobHandle job) => _job = job;

    /// <summary>A new empty job that ends its processes when closed, or null
    /// when Windows refuses one (the caller falls back to killing the tree).</summary>
    internal static ProcessTreeJob? TryCreate()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            AppLogger.For(nameof(ProcessTreeJob)).Warning("CreateJobObject failed (Win32 error {Err})", Marshal.GetLastWin32Error());
            job.Dispose();
            return null;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)length))
            {
                AppLogger.For(nameof(ProcessTreeJob)).Warning("SetInformationJobObject failed (Win32 error {Err})", Marshal.GetLastWin32Error());
                job.Dispose();
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return new ProcessTreeJob(job);
    }

    /// <summary>Puts <paramref name="process"/> (and what it starts from now
    /// on) in this job. False when Windows refuses or it already ended.</summary>
    internal bool TryAssign(Process process)
    {
        try
        {
            if (process.HasExited) return false;
            if (AssignProcessToJobObject(_job, process.SafeHandle)) return true;
            AppLogger.For(nameof(ProcessTreeJob)).Warning("AssignProcessToJobObject failed for pid {Pid} (Win32 error {Err})",
                process.Id, Marshal.GetLastWin32Error());
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLogger.For(nameof(ProcessTreeJob)).Warning(ex, "Could not put a restic process in its own job");
            return false;
        }
    }

    /// <summary>Ends every process in the job now.</summary>
    internal bool Terminate(uint exitCode)
    {
        if (_job.IsClosed) return false;
        if (TerminateJobObject(_job, exitCode)) return true;
        AppLogger.For(nameof(ProcessTreeJob)).Warning("TerminateJobObject failed (Win32 error {Err})", Marshal.GetLastWin32Error());
        return false;
    }

    /// <summary>Closing the job ends anything still in it.</summary>
    public void Dispose() => _job.Dispose();

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int ExtendedLimitInformation = 9;

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
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
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
    private static extern JobHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(JobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
