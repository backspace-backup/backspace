using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.OAuth;

/// <summary>Resolve the app folder and its containing drive using only the app-folder grant.</summary>
public sealed class OneDriveDiscovery
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _http;
    public OneDriveDiscovery(HttpClient? http = null) => _http = http ?? SharedHttp;

    public async Task<OneDriveIdentity> DiscoverAsync(string tokenJson, CancellationToken ct)
    {
        using var token = JsonDocument.Parse(tokenJson);
        if (!token.RootElement.TryGetProperty("access_token", out var access) || string.IsNullOrWhiteSpace(access.GetString()))
            throw new InvalidOperationException("OneDrive token has no access token. Sign in again.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://graph.microsoft.com/v1.0/me/drive/special/approot?$select=id,folder,specialFolder,parentReference");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.GetString());
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneDrive could not open Backspace's app folder (HTTP {(int)response.StatusCode}). " +
                "Check that this account has OneDrive and permits app-folder access. Backspace will not request full-drive access.");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = body.RootElement;
        var folderId = root.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
        var hasParent = root.TryGetProperty("parentReference", out var parent) && parent.ValueKind == JsonValueKind.Object;
        var id = hasParent && parent.TryGetProperty("driveId", out var driveId) && driveId.ValueKind == JsonValueKind.String ? driveId.GetString() : null;
        var type = hasParent && parent.TryGetProperty("driveType", out var driveType) && driveType.ValueKind == JsonValueKind.String ? driveType.GetString() : null;
        if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(id)
            || type is not ("personal" or "business" or "documentLibrary")
            || !root.TryGetProperty("folder", out var folder) || folder.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("OneDrive did not return a usable app folder and drive identity. Try connecting again.");
        return new OneDriveIdentity(id, type, folderId);
    }
}

public sealed record OneDriveIdentity(string Id, string DriveType, string AppFolderId);
