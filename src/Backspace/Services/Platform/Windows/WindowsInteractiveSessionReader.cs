using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Backspace.Services.Platform.Windows;

/// <summary>Locked and disconnected desktops are still signed in. Compare the
/// Windows logon timestamp as well as the session ID, since IDs can be reused.</summary>
internal sealed class WindowsInteractiveSessionReader(string? ownerSid = null) : IInteractiveSessionReader
{
    public bool Supported => true;
    public uint? BootId() => CurrentBootId();

    /// <summary>Windows counts its starts here; any account may read it.</summary>
    internal static uint? CurrentBootId()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters");
        return key?.GetValue("BootId") is int id ? unchecked((uint)id) : null;
    }
    public string AccountId { get; } = ownerSid ?? WindowsIdentity.GetCurrent().User!.Value;
    public InteractiveSessionIdentity? Current() => Read(Process.GetCurrentProcess().SessionId);
    public InteractiveSessionIdentity? Read(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, 24 /* WTSSessionInfo */, out var buffer, out var length))
        {
            var error = Marshal.GetLastWin32Error();
            // Windows also reports ERROR_INVALID_PARAMETER for a disappeared
            // session. Confirm absence through enumeration rather than treating
            // every failed query (including access denied) as a sign-out.
            if (SessionIsAbsent(sessionId)) return null;
            throw new Win32Exception(error, $"Could not check whether the Windows sign-in session still exists (Windows error {error}).");
        }
        try
        {
            if (length < Marshal.SizeOf<SessionInfo>()) throw new InvalidOperationException("Windows returned incomplete sign-in information.");
            var info = Marshal.PtrToStructure<SessionInfo>(buffer);
            if (string.IsNullOrEmpty(info.UserName) || info.LogonTime <= 0) return null;
            if (ownerSid is not null && ((SecurityIdentifier)new NTAccount(info.Domain, info.UserName)
                    .Translate(typeof(SecurityIdentifier))).Value != ownerSid) return null;
            return new(info.SessionId, info.LogonTime, (info.Domain + "\\" + info.UserName).ToUpperInvariant());
        }
        finally { WTSFreeMemory(buffer); }
    }

    internal static bool SessionIsAbsent(int sessionId)
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count)) return false;
        try
        {
            var size = Marshal.SizeOf<SessionEntry>();
            for (var i = 0; i < count; i++)
                if (Marshal.PtrToStructure<SessionEntry>(IntPtr.Add(buffer, i * size)).SessionId == sessionId) return false;
            return true;
        }
        finally { WTSFreeMemory(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SessionEntry { public int SessionId; public IntPtr WinStationName; public int State; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SessionInfo
    {
        public int State, SessionId;
        public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int length);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr buffer, out int count);
}
