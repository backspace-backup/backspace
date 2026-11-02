using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Backspace.Models;

namespace Backspace.Services.Platform.Windows;

/// <summary>
/// Backups to a network share run in the background service when the share's
/// user name and password are saved with the destination (U071, 1 November
/// 2026). The service runs under its own Windows account: it cannot see the
/// owner's mapped drive letters, nor use the password Windows remembers for
/// the owner, and a home NAS refuses the computer's own identity. Before each
/// run the worker connects to the share's network path with the saved
/// credentials, in the service's private logon session, and disconnects after.
/// Without saved credentials the backup stays with the tray app.
/// </summary>
internal static class ServiceNetworkShares
{
    /// <summary>A worker's user-facing reason for stopping before its engine
    /// started, read by the service host to explain the failed run.</summary>
    internal const string ReasonFile = "worker-reason.txt";

    /// <summary>
    /// Connects to every network share this backup uses, with the credentials
    /// saved in the service's vault. Disposing disconnects them. A share that
    /// refuses the credentials or cannot be reached throws
    /// <see cref="ShareConnectionException"/> with a message for the person.
    /// </summary>
    internal static IDisposable Connect(Backup backup, AppConfig config, SecretsStore vault, INetworkConnector? connector = null)
    {
        connector ??= new WindowsNetworkConnector();
        var connected = new List<string>();
        try
        {
            foreach (var target in backup.Targets)
            {
                var destination = config.Destinations.FirstOrDefault(d => d.Id == target.DestinationId);
                if (destination is not { Kind: DestinationKind.NetworkShare } || NetworkSharePath.ShareRoot(destination.PathOrSubpath) is not { } root
                    || connected.Contains(root, StringComparer.OrdinalIgnoreCase)) continue;
                if (!NetworkSharePath.ServiceCanReach(destination) || !vault.TryGet(destination.NetworkPasswordRef!, out var password) || password.Length == 0)
                    throw new ShareConnectionException($"No password is saved for «{destination.Name}». Edit the destination and enter the share's user name and password.");
                var code = connector.Connect(root, destination.NetworkUsername!.Trim(), password);
                if (code == ErrorSessionCredentialConflict)
                {
                    // An earlier connection of this account to the same server
                    // with other credentials; replace it.
                    connector.Disconnect(root);
                    code = connector.Connect(root, destination.NetworkUsername!.Trim(), password);
                }
                if (code != 0) throw new ShareConnectionException(Explain(code, destination, root));
                connected.Add(root);
            }
            return new Connections(connector, connected);
        }
        catch
        {
            foreach (var root in connected) connector.Disconnect(root);
            throw;
        }
    }

    internal const int ErrorSessionCredentialConflict = 1219;

    internal static string Explain(int code, Destination destination, string root) => code switch
    {
        5 or 86 or 1326 or 1327 or 1330 or 1331 or 1909 =>
            $"The network share «{destination.Name}» ({root}) refused the user name or password saved for it. Edit the destination to update them.",
        53 or 64 or 67 or 1203 or 1222 or 1231 or 1232 or 1311 =>
            $"The network share «{destination.Name}» ({root}) cannot be reached. Check that the device sharing it is on and connected to this network.",
        _ => $"Windows could not connect to the network share «{destination.Name}» ({root}): error {code}.",
    };

    private sealed class Connections(INetworkConnector connector, List<string> roots) : IDisposable
    {
        public void Dispose() { foreach (var root in roots) connector.Disconnect(root); roots.Clear(); }
    }

    internal interface INetworkConnector
    {
        int Connect(string root, string user, string password);
        void Disconnect(string root);
    }

    private sealed class WindowsNetworkConnector : INetworkConnector
    {
        public int Connect(string root, string user, string password)
        {
            var resource = new NetResource { Type = 1 /* RESOURCETYPE_DISK */, RemoteName = root };
            return WNetAddConnection2(ref resource, password, user, 4 /* CONNECT_TEMPORARY */);
        }

        public void Disconnect(string root) => _ = WNetCancelConnection2(root, 0, true);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NetResource
        {
            public int Scope, Type, DisplayType, Usage;
            public string? LocalName, RemoteName, Comment, Provider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(ref NetResource resource, string password, string user, int flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(string name, int flags, bool force);
    }

    /// <summary>The network path behind a mapped drive letter in this signed-in
    /// session (Z:\Backups → \\nas\share\Backups), or the path unchanged.</summary>
    internal static string ToNetworkPath(string path)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0])) return path;
        var size = 1024;
        var buffer = new System.Text.StringBuilder(size);
        if (WNetGetConnection(path[..2], buffer, ref size) != 0 || buffer.Length == 0) return path;
        var rest = path.Length > 2 ? path[2..].TrimStart('\\') : "";
        return rest.Length == 0 ? buffer.ToString() : buffer.ToString().TrimEnd('\\') + @"\" + rest;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, System.Text.StringBuilder remoteName, ref int length);
}

/// <summary>A network share the worker could not connect to, with the reason
/// for the person.</summary>
internal sealed class ShareConnectionException(string message) : IOException(message);
