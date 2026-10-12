# OAuth app registration guide

Backspace's OAuth flow for cloud destinations uses a per-provider
client_id. The Dropbox flow ships with Backspace's official
registration baked in (`xnmsuqfgzzr9gdw`). OneDrive and Google Drive
do **not** today — the slots in
[`OAuthAppRegistry.cs`](../src/Backspace/Services/OAuth/OAuthAppRegistry.cs)
are empty strings, the factory in
[`OAuthProviderFactory.cs`](../src/Backspace/Services/OAuth/OAuthProviderFactory.cs)
returns `null` for those kinds unless the user supplied their own
registration in Settings. The editor explains when a registration is missing.

This file is the registration guide. It exists for two audiences:

1. **The Backspace maintainer**, when ready to register Backspace's
   official OneDrive + Google Drive apps. The steps below produce the
   client_ids that go into `OAuthAppRegistry.OneDriveClientId` and
   `OAuthAppRegistry.GoogleDriveClientId`.
2. **End users who want their own** (BYO) — for example, an
   enterprise user whose IT policy requires their own client_id in
   their tenant, or a power user who wants to avoid Backspace's
   default rate-limit pool. The BYO path lands in
   `AppConfig.RcloneOAuthApps` via Settings → OAuth and overrides the
   baked-in client_id at runtime
   (see [`OAuthProviderFactory.For`](../src/Backspace/Services/OAuth/OAuthProviderFactory.cs)).

## OneDrive (Microsoft Identity Platform)

1. Open <https://entra.microsoft.com/> → **Identity** → **Applications**
   → **App registrations** → **New registration**.
2. Name: `Backspace` (or whatever the user wants for BYO).
3. Supported account types: **Accounts in any organizational
   directory and personal Microsoft accounts**.
4. Redirect URI: type **Public client / native (mobile & desktop)**,
   URI **`http://localhost/callback`**. The host and path match the
   OneDrive flow; its port is picked at runtime. Microsoft's native
   localhost matching ignores that port. A bare `http://localhost`
   registration does not match the `/callback` path. See
   [Microsoft's redirect URI rules](https://learn.microsoft.com/en-us/entra/identity-platform/reply-url#localhost-exceptions).
5. After registration, **Authentication** tab → **Allow public client
   flows = Yes** (PKCE without client secret).
6. **API permissions** → Add: `Files.ReadWrite.All` (delegated).
   Grant admin consent for the tenant if BYO; for the official
   registration, mark for "publisher verified" once enough users
   sign in.
7. Note the **Application (client) ID**. That's the value for
   `OneDriveClientId`.

No client secret — PKCE flow uses code+verifier only.

## Google Drive (Google Cloud Console)

1. Open <https://console.cloud.google.com/> → pick or create a
   project named `Backspace`.
2. **APIs & Services** → **Enabled APIs** → enable **Google Drive
   API**.
3. **OAuth consent screen** → **External**. Add a brand contact, fill
   the app name `Backspace`. For the official registration, request
   the **`https://www.googleapis.com/auth/drive.file`** scope (NOT
   the full `drive` scope). `drive.file` covers files the app created
   or the user explicitly authorized for it. Existing repositories
   created by another OAuth client may be inaccessible; do not
   promise arbitrary full-Drive repository browsing.
4. **Credentials** → **Create credentials** → **OAuth client ID** →
   Application type: **Desktop app**. Name: `Backspace`.
5. Note the **Client ID**. That's the value for `GoogleDriveClientId`.

The code uses Desktop-app PKCE without a client secret at code exchange.
Google currently documents that parameter as optional for installed
applications; validate the issued registration with a live account.
See [Google's installed-app flow](https://developers.google.com/identity/protocols/oauth2/native-app#exchange-authorization-code).

## Wiring shipped client_ids

In [`src/Backspace/Services/OAuth/OAuthAppRegistry.cs`](../src/Backspace/Services/OAuth/OAuthAppRegistry.cs):

```csharp
public const string OneDriveClientId    = "<paste the GUID from Entra here>";
public const string GoogleDriveClientId = "<paste the Client ID from Google here>";
```

`OneDriveOAuthProvider` and `GoogleDriveOAuthProvider` already exist and
are wired into `OAuthProviderFactory.For`. OneDrive now discovers the
signed-in user's default drive through Graph and stores its `drive_id`
and actual `drive_type`. It does not offer a SharePoint-site or multiple-drive
picker. Existing OneDrive destinations missing that identity discover it
on first use; an expired access token may require signing in again.

Google's OAuth request and rclone remote both use `drive.file`.
Production engine calls materialize an isolated config file per destination
under the `rclone-remotes` directory. This prevents one destination's rclone
writer from replacing another's configuration. Newer refreshed tokens are
imported into the encrypted vault before rendering; a later user sign-in
takes precedence over an older file. Legacy shared-config callers merge
remote sections rather than dropping unrelated destinations.

Deterministic tests exercise the editor's Connect command, local callback,
PKCE exchange, OneDrive discovery and rejected consent with simulated
provider responses. They also cover concurrent configuration writes and
token-refresh persistence. These do **not** validate real cloud accounts.
Before shipping official registrations, perform live sign-in, refresh across
an app restart, backup, listing and byte-verified restore with each provider.
Retain separate proof for Personal and Business OneDrive accounts and a
Google repository created under the intended application registration.

## BYO via Settings

For users who want their own client_id, the path is already there:

- Open **Settings → Cloud OAuth apps**, then find the provider.
- Paste the client ID and, if the registration requires one, its secret.
  Backspace stores the override in
  `AppConfig.RcloneOAuthApps` (one entry per provider family) and
  passes it through `OAuthProviderFactory.For` next time they
  connect.

The BYO infrastructure and provider classes are wired today. An override
provides the registration identity; the live-account checks above still apply.
