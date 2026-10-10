# GOTCHAS.md — known sharp edges

Upstream-driven traps and platform-specific surprises that aren't
obvious from the code. Read before touching the related subsystem.

## OAuth first-run flow (Dropbox / OneDrive / Google Drive)

`OAuthFlowCoordinator` runs a PKCE round-trip through a local listener.
Connect receives the token automatically; pasted tokens remain an advanced
fallback. OneDrive uses `http://localhost:<port>/callback` to match Entra's
native localhost registration rules. Other providers use `127.0.0.1`.
Closing the destination editor cancels an in-flight sign-in.

`DropboxAuthProbe` does an `rclone lsd` round-trip against a per-flow
temp `rclone.conf` before save so a stale or scope-mismatched token
fails loudly.

OneDrive instead queries Graph's default drive and persists its ID and
actual type before reporting connected. Google requests `drive.file`, and
its rclone remote uses the same scope; a captured Google token still needs
a real provider backup/list/restore check. Files created under another
OAuth client may not be visible. Official OneDrive and Google client IDs
remain unregistered; see [the registration guide](OAUTH_REGISTRATION.md).

Production engine calls use one rclone config per destination, under
`rclone-remotes`. Do not switch these back to the old shared file: rclone
writes refreshed tokens itself. The builder imports a newer on-disk token
into SecretsStore before rendering and preserves a newer interactive
sign-in. Shared-config compatibility writes merge unrelated remote sections
under a cross-process lock. No real cloud refresh/restart test has been
performed with the current registrations.

## Engine upgrades require a compatibility check

Build downloads use the versions in `src/Backspace/engine-versions.json`.
Do not replace those tags with `latest`: the release rehearsal on 9 October
2026 found that Backrest 1.14.1 changes the snapshot-file request from
`repo_guid` to `repo_id`, along with cancellation and command-response
formats. A backup succeeded, but restore browsing failed. The selected
Backrest version remains 1.13.0 until an intentional upgrade passes the
real backup, restore, cancellation and maintenance checks. Existing local
binaries are not overwritten by the downloader.

## Backrest 1.13.0 `SetConfig` guid contract

Guids must be 64-char hex; `auto_initialize=true` is rejected when a
guid is set; the validator reruns over the entire on-disk config on
every write, so an old 36-char UUID-shaped guid (Backspace pre-2026-06)
makes every `SetConfig` fail. Recovery is automatic via the
legacy-guid drop in `EnsurePlanAndRepoCoreAsync`.

## Backrest re-init contract

On first contact for a fresh destination, we send `Guid = ""` +
`AutoInitialize = true` and let Backrest assign the canonical 64-char
guid by adopting the on-disk restic repo (or initialising one).
Subsequent `SetConfig` passes leave the existing repo stanza alone.
Never round-trip our own deterministic guid through `SetConfig` —
restic's actual config-id is the source of truth.

## Mounted snapshot browsing is Windows-only today

The production mount path uses `WinFspMountCoordinator` and a custom
read-only filesystem, not the former WebDAV mount implementation.
It requires the WinFSP runtime on Windows. On macOS / Linux the matching
path will need a FUSE-backed implementation. In-app snapshot browsing is
a separate feature and does not require a mounted drive.

## Healthchecks.io management API rate limits

Management calls log HTTP failures and return an unsuccessful result; they
do not currently retry a batch of check creations. A rejected creation must
not be shown as a connected check. Terminal run pings happen before email
delivery and outside the per-status daily human-alert cap. Use the exact
`ping_url` returned by management/verification for self-hosted services;
a bare legacy UUID still means the public hc-ping.com endpoint.

## macOS / Linux untested

The cross-platform TFM compiles and the platform abstractions
(`launchd`, `systemd`, AES-GCM keyfile, `geteuid`) are in place, but
the suite has **not been exercised** on either OS. Expect papercuts the
first time someone tries — treat any Unix path as untested until it has
a CI run against it.

## `NumericUpDown.Value` is `decimal?` in Avalonia 11

We bind it to plain `int` properties and rely on default numeric
conversion. Works in practice; wrap in a converter if it ever throws.

## Restic `--include` cannot match `[` or `]` literally

Restic's include matcher runs Go's `path/filepath.Match`. On Windows
the backslash escape is unavailable (`\` is a path separator there) AND
restic's `ValidatePatterns` rejects the POSIX class self-escape `[[]` /
`[]]` as `invalid pattern(s)`. There is currently no way to express a
literal `[` or `]` as a match character on Windows.

Backspace substitutes each bracket with `?` for restic's include matcher.
That can also match same-length sibling names, so every restore writes
into an isolated staging directory first. Only validated requested paths
are published to the target after a successful or explicitly partial
terminal result. Unrelated existing target files are never swept.
Failed or cancelled staged output is not published. Existing target
junctions and other reparse points are rejected before publishing.

The [Backrest Windows bracket fix](https://github.com/garethgeorge/backrest/pull/1254)
was merged on June 30, 2026. It changes Backrest's native Restore RPC;
Backspace uses native Restore for a single selected file or folder when no
per-backup bandwidth limit is set. Its target must not exist and its plan ID
must be supplied. The output is still staged and filtered before publication;
native progress comes from the operation's structured status. Scattered
selections and bandwidth-limited restores retain RunCommand with quiet JSON:
this avoids Backrest's 2 MB command-output limit, but provides final counts
rather than live percentages. Native Restore cannot accept several paths or
a per-request bandwidth limit. The proposed [restic literal include flag](https://github.com/restic/restic/pull/21872)
was still open when checked on October 2, 2026. Only retire our wildcard
workaround after a bundled restic version supports literal includes and
the occupied-target preservation tests still pass.

## Repository command lifetime and cancellation

Some Backrest RPCs hold their HTTP response until restic finishes.
Cancelling that HTTP request is not confirmation that restic stopped.
Track the actual operation, request daemon cancellation with an independent
token, and hold the operation's read lease until terminal acknowledgement.
If ownership cannot be resolved, fail closed until restart rather than
unlocking maintenance over a possibly live writer. GetLogs returns a
protobuf BytesValue: decode its base64 payload before parsing restic JSON.

## Password changes and test restore

Password changes require exclusive repository ownership. Never force-remove
an active repository lock. Save a pending encrypted recovery password
before mutating the repository, and finish secret persistence even if the
user closes or cancels after mutation starts.

Test restore compares every eligible file from each source and target in
bounded batches, including empty files. Source files changed since the
snapshot cannot be called byte-identical; the result labels their weaker
comparison. Invalid relative paths and linked directories must not escape
the scratch directory during comparison or cleanup.
