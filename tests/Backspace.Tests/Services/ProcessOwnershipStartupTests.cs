using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Backspace.Services;
using Backspace.Tests.TestHelpers;
using Xunit;

namespace Backspace.Tests.Services;

public sealed class ProcessOwnershipStartupTests
{
    [Fact]
    public void Suspended_child_has_a_verified_ownership_record_before_its_loader_runs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TempWorkspace();
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        Assert.True(CreateProcess(executable, new StringBuilder($"\"{executable}\" /d /c exit 0"),
            IntPtr.Zero, IntPtr.Zero, false, 0x00000004 /* CREATE_SUSPENDED */ | 0x08000000 /* CREATE_NO_WINDOW */,
            IntPtr.Zero, null, ref startup, out var created), new Win32Exception(Marshal.GetLastWin32Error()).Message);
        try
        {
            using var process = Process.GetProcessById(created.ProcessId);
            // The exact starting state that defeated the old MainModule lookup.
            try { Assert.Null(process.MainModule); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 299)
            {
                // Windows also reports ERROR_PARTIAL_COPY while the suspended
                // child's module list is still uninitialized.
            }
            var ownership = OwnedProcessRegistry.CaptureOwnership(process);
            Assert.Equal(created.ProcessId, ownership.Child.Pid);
            Assert.Equal(Path.GetFullPath(executable), ownership.Child.BinaryPath, ignoreCase: true);
            Assert.Equal(OwnedProcessRegistry.Presence.Matching, OwnedProcessRegistry.Inspect(ownership.Child));
            Assert.Equal(OwnedProcessRegistry.Presence.Different,
                OwnedProcessRegistry.Inspect(ownership.Child with { StartedUtcTicks = ownership.Child.StartedUtcTicks + 1 }));
            Assert.Equal(OwnedProcessRegistry.Presence.Different,
                OwnedProcessRegistry.Inspect(ownership.Child with { BinaryPath = executable + ".unrelated" }));
            var record = Path.Combine(workspace.Root, "sidecar.pid");
            OwnedProcessRegistry.Write(record, ownership);
            OwnedProcessRegistry.Reap(record, terminate: _ => throw new Exception("The owner is still alive."));
            Assert.Equal(ownership, OwnedProcessRegistry.Read(record));
            Assert.False(process.HasExited);
        }
        finally
        {
            // Only the exact suspended process handle returned by this test.
            // Its primary thread never runs, including if an assertion fails.
            TerminateProcess(created.Process, 0);
            WaitForSingleObject(created.Process, 5000);
            CloseHandle(created.Thread);
            CloseHandle(created.Process);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        int creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startup,
        out ProcessInformation process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
