using System;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>Network share paths and whether the background service can reach
/// a share destination on its own (U071).</summary>
public static class NetworkSharePath
{
    /// <summary>The share a network path belongs to: \\server\share, or null
    /// for anything else (a drive letter, a device path).</summary>
    public static string? ShareRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] : null;
    }

    /// <summary>The service runs under its own Windows account, so it reaches a
    /// share only by its network path, with a saved user name and password.</summary>
    public static bool ServiceCanReach(Destination destination) =>
        destination.Kind == DestinationKind.NetworkShare && ShareRoot(destination.PathOrSubpath) is not null
        && !string.IsNullOrWhiteSpace(destination.NetworkUsername) && !string.IsNullOrWhiteSpace(destination.NetworkPasswordRef);
}
