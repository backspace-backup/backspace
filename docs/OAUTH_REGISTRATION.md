# OAuth app registration guide

Backspace's OAuth flow for cloud destinations uses a per-provider
client_id. Dropbox uses Backspace's official registration
(`xnmsuqfgzzr9gdw`); OneDrive uses
`d23e7f5b-7a24-4a74-88f8-37990e6e066a`. These are public identifiers.
Google Drive uses
`470920767794-287o09bvro3keb6hqrjenafetkbp62cb.apps.googleusercontent.com`
in the owner-controlled `backspace-511510` project. The public IDs ship in
[`OAuthAppRegistry.cs`](../src/Backspace/Services/OAuth/OAuthAppRegistry.cs).
They identify Backspace; they do not grant access to anyone's account.
Google also issues a desktop-app `client_secret`, which ships with its
matching client ID. An installed app cannot keep this shared credential
confidential; it is not a user's password or account access token. Google
requires it alongside PKCE during code exchange and for token renewal.
Account access and refresh tokens are issued only after consent and belong
in that user's encrypted local vault, never in a build or source control.

This file is the registration guide. It exists for two audiences:

1. **The Backspace maintainer**, when creating or replacing Backspace's
   official cloud registrations. The steps below produce the
   client_ids that go into `OAuthAppRegistry.OneDriveClientId` and
   `OAuthAppRegistry.GoogleDriveClientId`.
2. **End users who want their own** (BYO) — for example, an
   enterprise user whose IT policy requires their own client_id in
   their tenant, or a power user who wants to avoid Backspace's
   default rate-limit pool. The BYO path lands in
   `AppConfig.RcloneOAuthApps` via Settings → Cloud OAuth apps and overrides the
   baked-in client_id at runtime
   (see [`OAuthProviderFactory.For`](../src/Backspace/Services/OAuth/OAuthProviderFactory.cs)).

## Dropbox

Use a Scoped App with **App folder** access and allow public clients
(PKCE). Register **`http://localhost:53682/callback`** exactly, including
the port and path. Dropbox does not accept the arbitrary local ports that
Microsoft and Google allow. If that port is occupied, Backspace reports
the conflict before opening a sign-in page.

Enable `account_info.read`, `files.metadata.read`, `files.content.read`
and `files.content.write`. These cover account capacity, folder listings,
backup and restore; file access stays inside `/Apps/Backspace`. Backspace
requests `token_access_type=offline` to obtain a refresh token. It does
not need sharing permissions or an embedded app secret. See
[Dropbox's OAuth guide](https://docs.dropboxapi.com/dropbox-api/docs/oauth).

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
   add **`Files.ReadWrite.AppFolder`** only. Remove the default `User.Read`
   permission and any earlier `Files.ReadWrite.All` permission. The app requests `offline_access` during
   sign-in so scheduled backups can refresh access. Delegated file access
   does not require admin consent by default, but an organization's consent
   policy can require it. Do not select the application-level permission,
   which grants access without a signed-in user. See
   [Microsoft's app-folder guide](https://learn.microsoft.com/en-us/graph/onedrive-sharepoint-appfolder)
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
   **`https://www.googleapis.com/auth/drive.appdata`** only. This grants access
   to Backspace's private app-data folder. Do not use `drive.file` as a
   substitute: it limits files, but does not enforce this folder boundary.
4. **Google Auth Platform** → **Clients** → **Create client** →
   Application type: **Desktop app**. Name: `Backspace`. In the older console
   layout, this is under **APIs & Services** → **Credentials**.
5. Keep the **Client ID** and the generated desktop-app **Client secret**
   together. These are the values for `GoogleDriveClientId` and
   `GoogleDriveClientSecret`. Handle downloaded credentials locally;
   do not paste the credentials JSON into a chat or Review desk comment.

The code uses Desktop-app PKCE and sends the matching desktop credential
to Google's token endpoint. The live Backspace registration rejected a
code exchange without it with `client_secret is missing`. Although Google's
parameter table calls the field optional, do not omit it for this
registration. The same pair must reach rclone for later token renewal.
See [Google's installed-app flow](https://developers.google.com/identity/protocols/oauth2/native-app#exchange-authorization-code).

An External app left in **Testing** receives refresh tokens that expire
after seven days for Drive access. That is suitable for initial checks,
not unattended backups over the long term. Before release, the registration
owner must move the app to **In production** and satisfy any branding or
verification requirements Google presents. `drive.appdata` is non-sensitive;
it does not require the same scope review as full-Drive access. See
[Google's refresh-token limits](https://developers.google.com/identity/protocols/oauth2#expiration)
and [Drive scope requirements](https://developers.google.com/workspace/drive/api/guides/api-specific-auth).

## Wiring shipped client_ids

In [`src/Backspace/Services/OAuth/OAuthAppRegistry.cs`](../src/Backspace/Services/OAuth/OAuthAppRegistry.cs):

```csharp
public const string OneDriveClientId    = "<paste the GUID from Entra here>";
public const string GoogleDriveClientId = "<paste the Client ID from Google here>";
public const string GoogleDriveClientSecret = "<matching desktop-app credential>";
```

`OneDriveOAuthProvider` and `GoogleDriveOAuthProvider` already exist and
are wired into `OAuthProviderFactory.For`. OneDrive discovers
`/me/drive/special/approot` and stores the returned folder ID, containing
drive ID and actual drive type. Its rclone remote uses that folder as
`root_folder_id` and requests only `Files.ReadWrite.AppFolder offline_access`.
It does not offer a SharePoint-site or multiple-drive picker. A failed
app-folder request must never fall back to a broader permission.

Google requests `drive.appdata`; rclone calls this scope `drive.appfolder`.
The remote root is `appDataFolder`. Its files cannot be trashed, so the
remote sets `use_trash = false` for restic's repository maintenance.
Google hides this folder from its normal file list and other apps.
Deleting Backspace's application data in Google deletes these backups.
Changing the Google application registration also changes which private
folder is accessible; preserve the registration when upgrading the app.
See [Google's app-data constraints](https://developers.google.com/workspace/drive/api/guides/appdata).

Older OneDrive and Google destinations have no `CloudAppFolderOnly` marker.
Backspace refuses to change their storage root silently or generate a
broader remote. Create a new app-folder destination; existing repositories
are left untouched. A newly issued grant must include the narrow scope and
no broader file permission before the editor can save it.
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
Current OneDrive support is Personal only; Business is not offered in the
destination selector. If Business support is added later, it needs separate
real-account proof. Google needs a repository created under the intended
application registration.

## BYO via Settings

For users who want their own client_id, the path is already there:

- Open **Settings → Cloud OAuth apps**, then find the provider.
- Paste the client ID and, if the registration requires one, its secret.
  Backspace stores the override in
  `AppConfig.RcloneOAuthApps` (one entry per provider family) and
  passes it through `OAuthProviderFactory.For` next time they
  connect. Google uses this matching pair for both code exchange and rclone's
  later token renewal; a custom ID never borrows the official app's secret.

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
every file's bytes. OneDrive Personal proof does not establish Business support.
Google needs a repository created with the intended app registration because
its private app-data folder belongs to that registration.
