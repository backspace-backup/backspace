using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Backspace.Services.Platform.Windows;

/// <summary>Which process listens on a 127.0.0.1 port (U152). Backspace picks
/// the engine's port, releases it and starts the engine on it: in between,
/// another account could take the port and collect the engine's sign-in
/// password and repository passwords. Nothing is sent before the listener is
/// confirmed to be the engine Backspace started.</summary>
[SupportedOSPlatform("windows")]
internal static class LoopbackPortOwner
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    /// <summary>The process listening on 127.0.0.1:<paramref name="port"/>
    /// (or on every address at that port), or null when none is.</summary>
    internal static int? Of(int port)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (result == 0) break;
                if (result != 122 || attempt > 4) throw new Win32Exception((int)result); // 122: buffer too small
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(size);
            }
            var count = Marshal.ReadInt32(buffer);
            const int rowSize = 24; // state, local address, local port, remote address, remote port, owning process
            var loopback = BitConverter.ToUInt32(IPAddress.Loopback.GetAddressBytes(), 0);
            int? any = null;
            for (var i = 0; i < count; i++)
            {
                var row = buffer + 4 + i * rowSize;
                var address = (uint)Marshal.ReadInt32(row, 4);
                var rowPort = (ushort)IPAddress.NetworkToHostOrder((short)(Marshal.ReadInt32(row, 8) & 0xFFFF));
                if (rowPort != port) continue;
                var owner = Marshal.ReadInt32(row, 20);
                if (address == loopback) return owner;
                if (address == 0) any = owner;
            }
            return any;
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }
}
