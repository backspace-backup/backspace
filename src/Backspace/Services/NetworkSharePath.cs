using System;
using System.Linq;
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

    /// <summary>With the background service set up, it runs every backup
    /// (U171). Names the network shares backups use that it can't sign in to,
    /// for Settings → Administrator access; empty when there are none.</summary>
    public static string ServiceSignInNotice(AppConfig config)
    {
        var used = config.Backups.SelectMany(b => b.Targets).Select(t => t.DestinationId).ToHashSet(StringComparer.Ordinal);
        var names = config.Destinations
            .Where(d => d.Kind == DestinationKind.NetworkShare && used.Contains(d.Id) && !ServiceCanReach(d))
            .Select(d => "«" + d.Name + "»").ToList();
        if (names.Count == 0) return "";
        var list = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        return $"The background service runs every backup and signs in to network shares with their own user name and password. Edit {list} in Destinations and enter them, or backups to {(names.Count == 1 ? "it" : "them")} fail.";
    }
}
