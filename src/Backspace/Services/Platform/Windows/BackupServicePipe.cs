using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Local transport for the passwordless backup service. This boundary is not
/// an installer or a command dispatcher: enabling it must not register S4U
/// tasks, accept executable paths, or change the desktop vault.
/// </summary>
internal static class BackupServicePipe
{
    internal const int MaximumMessageBytes = 1024 * 1024;
    // Deliberately omit FILE_CREATE_PIPE_INSTANCE (4). GENERIC_WRITE includes it.
    internal const uint ClientAccess = 0x0012019b;
    private const uint Overlapped = 0x40000000, FirstInstance = 0x00080000;
    private const uint RejectRemoteClients = 8;

    internal static string SecurityDescriptor(string serviceSid, string ownerSid)
    {
        var service = new SecurityIdentifier(serviceSid);
        var owner = new SecurityIdentifier(ownerSid);
        if (!service.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal)
            || service.Equals(owner) || !owner.IsAccountSid())
            throw new ArgumentException("A separate virtual service identity and account owner are required.");
        // Only the configured account can connect as an ordinary user. The
        // service and SYSTEM are trusted; no Everyone/Authenticated Users ACE.
        return $"O:{service.Value}G:{service.Value}D:P(A;;GA;;;{service.Value})(A;;GA;;;SY)(A;;0x{ClientAccess:x};;;{owner.Value})";
    }

    internal static NamedPipeServerStream CreateListener(string name, string serviceSid, string ownerSid)
    {
        var fullName = PipePath(name);
        var descriptor = SecurityDescriptor(serviceSid, ownerSid);
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != serviceSid)
            throw new SecurityException("Only the configured service identity may create this endpoint.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(descriptor, 1, out var security, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = security };
            var handle = CreateNamedPipe(fullName, 3 | Overlapped | FirstInstance,
                RejectRemoteClients, 1, 65536, 65536, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(security); }
    }

    internal static async Task<NamedPipeClientStream> ConnectAsync(string name, string serviceSid,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var fullName = PipePath(name);
        var expected = new SecurityIdentifier(serviceSid);
        if (!expected.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal))
            throw new ArgumentException("A virtual service identity is required.");
        using var deadline = Deadline(timeout, cancellationToken);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            // Identification only: a fake endpoint must never impersonate the
            // desktop account. No data is sent before kernel-owner validation.
            var handle = CreateFile(fullName, ClientAccess, 0, IntPtr.Zero, 3,
                Overlapped | 0x00100000 | 0x00010000, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try
                {
                    VerifyOwner(handle, expected);
                    return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
                }
                catch { handle.Dispose(); throw; }
            }
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is not (2 or 231)) throw new Win32Exception(error);
            await Task.Delay(25, deadline.Token).ConfigureAwait(false);
        }
    }

    private static void VerifyOwner(SafePipeHandle handle, SecurityIdentifier expected)
    {
        // A pipe name or a claimed PID is not authentication. An unprivileged
        // process cannot assign a kernel object's owner to another account.
        // Administrators/SYSTEM can defeat this boundary and are trusted.
        var error = GetSecurityInfo(handle, 6, 1, out var owner, out _, out _, out _, out var descriptor);
        try
        {
            if (error != 0) throw new Win32Exception(checked((int)error));
            if (owner == IntPtr.Zero || !new SecurityIdentifier(owner).Equals(expected))
                throw new SecurityException("The endpoint does not belong to the configured backup service.");
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    internal static async Task<byte[]> ReadMessageAsync(Stream stream, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = Deadline(timeout, token);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, deadline.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid service message length.");
        var message = new byte[length];
        await stream.ReadExactlyAsync(message, deadline.Token).ConfigureAwait(false);
        return message;
    }

    internal static async Task WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> message,
        TimeSpan timeout, CancellationToken token)
    {
        if (message.Length is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid service message length.");
        using var deadline = Deadline(timeout, token);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
        await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(message, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    /// <summary>Wait for receipt before the server disconnects. Completing a
    /// pipe write only places bytes in the buffer; Disconnect can discard
    /// those bytes before the client reads them.</summary>
    internal static async Task SendResponseAsync(Stream stream, ReadOnlyMemory<byte> response,
        TimeSpan timeout, CancellationToken token)
    {
        using var deadline = Deadline(timeout, token);
        await WriteMessageAsync(stream, response, timeout, deadline.Token).ConfigureAwait(false);
        var acknowledgment = new byte[1];
        await stream.ReadExactlyAsync(acknowledgment, deadline.Token).ConfigureAwait(false);
        if (acknowledgment[0] != 6) throw new InvalidDataException("Invalid service response acknowledgment.");
    }

    internal static async Task<byte[]> ReadResponseAsync(Stream stream, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = Deadline(timeout, token);
        var response = await ReadMessageAsync(stream, timeout, deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { 6 }, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
        return response;
    }

    private static CancellationTokenSource Deadline(TimeSpan timeout, CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(timeout);
        return source;
    }

    private static string PipePath(string name)
    {
        if (name.Length is < 1 or > 180) throw new ArgumentException("Invalid service pipe name.");
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-'))
                throw new ArgumentException("Invalid service pipe name.");
        return @"\\.\pipe\" + name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int InheritHandle; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outputSize, uint inputSize, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
