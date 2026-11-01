using System;
using System.Linq;
using System.Text.Json;
using Backspace.Models;

namespace Backspace.Services.OAuth;

/// <summary>Checks new grants and prevents older destinations from silently changing roots.</summary>
internal static class CloudFolderAccess
{
    internal const string GoogleScope = "https://www.googleapis.com/auth/drive.appdata";
    internal const string OneDriveScope = "Files.ReadWrite.AppFolder";

    internal static bool AppliesTo(DestinationKind kind) => kind is DestinationKind.GoogleDrive
        or DestinationKind.OneDrivePersonal or DestinationKind.OneDriveBusiness;

    internal static string RequireScope(JsonElement token, DestinationKind kind)
    {
        var scope = token.TryGetProperty("scope", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expected = kind == DestinationKind.GoogleDrive ? GoogleScope : OneDriveScope;
        // Microsoft may qualify Graph scopes with the resource URL.
        var normalized = scopes.Select(s => s.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase)
            ? s["https://graph.microsoft.com/".Length..] : s).ToArray();
        if (!normalized.Contains(expected, StringComparer.OrdinalIgnoreCase)
            || normalized.Any(s => kind == DestinationKind.GoogleDrive
                ? !s.Equals(expected, StringComparison.Ordinal)
                : !s.Equals(expected, StringComparison.OrdinalIgnoreCase) && s is not ("offline_access" or "openid" or "profile" or "email")))
            throw new InvalidOperationException("The account did not grant access limited to Backspace's own folder. " +
                "Remove any earlier Backspace access in your cloud account, then connect again. Full-drive access will not be used.");
        return scope;
    }

    internal static void RequireDestination(Destination destination)
    {
        if (AppliesTo(destination.Kind) && !destination.CloudAppFolderOnly)
            throw new InvalidOperationException("This destination uses an older cloud connection. Create a new destination " +
                "and connect it to Backspace's own app folder. Your existing backups have not been moved or deleted.");
    }
}
