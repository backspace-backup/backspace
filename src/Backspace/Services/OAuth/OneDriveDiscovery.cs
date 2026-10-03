using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Backspace.Services.OAuth;

/// <summary>Resolve the signed-in user's default OneDrive, not a guessed drive type.</summary>
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
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive?$select=id,driveType,name");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.GetString());
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneDrive drive discovery returned HTTP {(int)response.StatusCode}. Check the account has OneDrive and sign in again.");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var id = body.RootElement.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        var type = body.RootElement.TryGetProperty("driveType", out var typeValue) ? typeValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || type is not ("personal" or "business" or "documentLibrary"))
            throw new InvalidOperationException("OneDrive did not return a usable drive identity. Try signing in again.");
        return new OneDriveIdentity(id, type);
    }
}

public sealed record OneDriveIdentity(string Id, string DriveType);
