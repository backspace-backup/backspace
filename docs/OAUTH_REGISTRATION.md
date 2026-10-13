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
   `AppConfig.RcloneOAuthApps` via Settings → Cloud OAuth apps and overrides the
   baked-in client_id at runtime
   (see [`OAuthProviderFactory.For`](../src/Backspace/Services/OAuth/OAuthProviderFactory.cs)).

## OneDrive (Microsoft Identity Platform)

1. Sign in to <https://entra.microsoft.com/> with the account that will own
   Backspace's registration. Select its tenant, then **Entra ID** →
   **App registrations** → **New registration**. The account needs permission
   to register apps, such as the Application Developer role. See
   [Microsoft's registration guide](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app).
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
6. **API permissions** → **Microsoft Graph** → **Delegated permissions** →
   add `Files.ReadWrite.All`. The app also requests `offline_access` during
   sign-in so scheduled backups can refresh access. Delegated file access
   does not require admin consent by default, but an organization's consent
   policy can require it. Do not select the application-level permission,
   which grants access without a signed-in user. See
   [Microsoft's permission reference](https://learn.microsoft.com/en-us/graph/permissions-reference#filesreadwriteall)
   and [consent rules](https://learn.microsoft.com/en-us/graph/permissions-overview).
7. Note the **Application (client) ID**. That's the value for
   `OneDriveClientId`.

No client secret — PKCE flow uses code+verifier only.

For public distribution, review
[publisher verification](https://learn.microsoft.com/en-us/entra/identity-platform/publisher-verification-overview)
with the registration owner. It requires eligible Partner Center and domain
details; it is not unlocked by accumulating users. This is separate from
creating an app ID and testing a personal account.

## Google Drive (Google Cloud Console)

1. Open <https://console.cloud.google.com/> → pick or create a
   project named `Backspace`.
2. **APIs & Services** → **Enabled APIs** → enable **Google Drive
   API**.
3. **Google Auth Platform** → **Branding**: enter the app name `Backspace`
   and owner-controlled support and contact details. Under **Audience**, use
   **External** so consumer accounts can connect, and add the disposable test
   account while the app is in Testing. Under **Data Access**, request
   the **`https://www.googleapis.com/auth/drive.file`** scope (NOT
   the full `drive` scope). `drive.file` covers files the app created
   or the user explicitly authorized for it. Existing repositories
   created by another OAuth client may be inaccessible; do not
   promise arbitrary full-Drive repository browsing.
4. **Google Auth Platform** → **Clients** → **Create client** →
   Application type: **Desktop app**. Name: `Backspace`. In the older console
   layout, this is under **APIs & Services** → **Credentials**.
5. Note the **Client ID**. That's the value for `GoogleDriveClientId`.

The code uses Desktop-app PKCE without a client secret at code exchange.
Google currently documents that parameter as optional for installed
applications; validate the issued registration with a live account.
See [Google's installed-app flow](https://developers.google.com/identity/protocols/oauth2/native-app#exchange-authorization-code).

An External app left in **Testing** receives refresh tokens that expire
after seven days for Drive access. That is suitable for initial checks,
not unattended backups over the long term. Before release, the registration
owner must move the app to **In production** and satisfy any branding or
verification requirements Google presents. `drive.file` is non-sensitive;
it does not require the same scope review as full-Drive access. See
[Google's refresh-token limits](https://developers.google.com/identity/protocols/oauth2#expiration)
and [Drive scope requirements](https://developers.google.com/workspace/drive/api/guides/api-specific-auth).

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

## What the registration owner supplies

The owner chooses the Microsoft tenant and Google project, enters the
publisher/contact details, and completes the provider's sign-in and consent
screens. The agent can then wire the issued **client IDs** into a build and
run the remaining checks with an authorized disposable account. Do not put
account passwords, refresh tokens, or downloaded credential JSON in a Review
desk comment. Client IDs themselves are public application identifiers.

For each provider, use a new empty test destination. Connect, back up known
files, close and reopen Backspace, allow the access token to expire so refresh
is exercised, list the backup, then restore to another folder and compare
every file's bytes. OneDrive needs separate Personal and Business coverage.
Google needs a repository created with the intended app registration because
`drive.file` does not grant access to arbitrary existing repositories.
