using System.Collections.Generic;
using System.Linq;
using Backspace.Models;

namespace Backspace.Services;

/// <summary>
/// Whether a destination has what a backup needs before it can start: the
/// cloud sign-in, S3 keys and, for an encrypted destination, a storage
/// password. Imported Duplicacy destinations have none of these yet
/// (sign-ins and passwords are not imported). Run now opens the destination
/// to finish them, and a scheduled run stops with this reason instead of
/// retrying restic four times (U082, 2 November 2026).
/// </summary>
public static class DestinationReadiness
{
    public static string? Problem(Destination destination, SecretsStore secrets)
        => Steps(destination, secrets) is { } steps ? $"«{destination.Name}» isn't set up yet: {steps}. Open it from Destinations to finish." : null;

    /// <summary>What is left to do, as one phrase, or null when nothing is.</summary>
    public static string? Steps(Destination destination, SecretsStore secrets)
    {
        var missing = Missing(destination, secrets).ToList();
        if (missing.Count == 0) return null;
        return missing.Count == 1 ? missing[0] : string.Join(", ", missing.Take(missing.Count - 1)) + " and " + missing[^1];
    }

    public static IEnumerable<string> Missing(Destination destination, SecretsStore secrets)
    {
        if (destination.NeedsOAuth && !Has(secrets, destination.OAuthTokenRef))
            yield return $"connect your {ProviderName(destination.Kind)} account";
        if (destination.Kind == DestinationKind.S3Compatible
            && (!Has(secrets, destination.S3AccessKeyRef) || !Has(secrets, destination.S3SecretKeyRef)))
            yield return "enter its access keys";
        if (destination.Encrypted && !Has(secrets, destination.StoragePasswordRef))
            yield return "choose a storage password";
    }

    private static bool Has(SecretsStore secrets, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        try { return secrets.TryGet(reference, out var value) && !string.IsNullOrEmpty(value); }
        catch (System.Exception) { return true; } // An unreadable vault is reported by the run itself.
    }

    private static string ProviderName(DestinationKind kind) => kind switch
    {
        DestinationKind.DropboxAppScoped or DestinationKind.DropboxFullAccess => "Dropbox",
        DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness => "OneDrive",
        DestinationKind.GoogleDrive => "Google Drive",
        _ => "cloud",
    };
}
