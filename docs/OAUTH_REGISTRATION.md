# OAuth app registration guide

Backspace's OAuth flow for cloud destinations uses a per-provider
client_id. The Dropbox flow ships with Backspace's official
registration baked in (`xnmsuqfgzzr9gdw`). OneDrive and Google Drive
do **not** today — the slots in
[`OAuthAppRegistry.cs`](../src/Backspace/Services/OAuth/OAuthAppRegistry.cs)
are empty strings, the factory in
[`OAuthProviderFactory.cs`](../src/Backspace/Services/OAuth/OAuthProviderFactory.cs)
returns `null` for those kinds, and the UI surfaces "OAuth not yet
wired" when the user tries to connect.

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
   URI **`http://localhost`** (the actual port is picked at runtime
   by `OAuthFlowCoordinator`).
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
   the full `drive` scope — the latter triggers Google's CASA
   Tier-2 security audit). `drive.file` covers files the app created
   or the user explicitly opened with our app, which fits the
   restic-restore model.
4. **Credentials** → **Create credentials** → **OAuth client ID** →
   Application type: **Desktop app**. Name: `Backspace`.
5. Note the **Client ID**. That's the value for `GoogleDriveClientId`.

No client secret needed — the Desktop app flow is PKCE-only and the
secret embedded in the JSON download is non-confidential (Google
documents this; the OAuth spec treats native app secrets as public).

## Wiring shipped client_ids

In [`src/Backspace/Services/OAuth/OAuthAppRegistry.cs`](../src/Backspace/Services/OAuth/OAuthAppRegistry.cs):

```csharp
public const string OneDriveClientId    = "<paste the GUID from Entra here>";
public const string GoogleDriveClientId = "<paste the Client ID from Google here>";
```

Then implement `OneDriveOAuthProvider` + `GoogleDriveOAuthProvider`
(mirror `DropboxOAuthProvider`'s shape — see ROADMAP entry "OneDrive
+ Google Drive OAuth providers") and wire them into
`OAuthProviderFactory.For`.

## BYO via Settings

For users who want their own client_id, the path is already there:

- Settings → OAuth tab (or per-destination editor) → "Use my own
  client_id" toggle.
- The user pastes their client_id; Backspace stores it in
  `AppConfig.RcloneOAuthApps` (one entry per provider family) and
  passes it through `OAuthProviderFactory.For` next time they
  connect.

The BYO infrastructure is wired today; it just has no providers to
flow into for OneDrive + Google Drive. Once those providers ship,
BYO works for them too with no additional code.
