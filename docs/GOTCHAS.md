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
The current pins are Backrest 1.14.1 and restic 0.19.1. Do not replace
those tags with `latest`: a successful backup alone does not establish
compatibility. Backrest 1.14.1 lists snapshot files using its configured
`repo_id`; operation queries still use the canonical repository guid.
Cancellation takes `operationId`, and scheduled-task responses return
that same field. Maintenance and Restore can acknowledge scheduling before
the operation appears in polling results. Wait for the identified operation
to finish; an HTTP success is not a completed backup, restore or check.
Existing local binaries are not overwritten by the downloader, so an upgrade
must replace them deliberately and verify the versions in the test output
directory as well as in the source assets. Run real backup, restore,
cancellation and maintenance checks against those binaries.

## Backrest 1.13.0+ `SetConfig` guid contract

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

Mounted-file caches are separated by destination, source and immutable snapshot
ID. A revision number is only a display position and can change after retention;
never use it to reuse bytes from an earlier mount. Bulk reads restore into a
private directory and publish it by directory rename only after complete
success. Failed or cancelled bulk output is never served merely because a file
exists. Snapshot listing and restore processes retain their repository lease
until process exit is confirmed; a cancelled HTTP wait alone is not sufficient.
Mounted reads refuse links and linked parent folders, which could otherwise
expose today's local files outside the snapshot. Restore can recover the link
itself; mounted browsing does not currently emulate its filesystem semantics.

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

## `NumericUpDown.Value` is `decimal?`

We bind it to plain `int` properties and rely on default numeric
conversion. Works in practice; wrap in a converter if it ever throws.

## Literal brackets in restore selections on Windows

Restic's include matcher runs Go's `path/filepath.Match`. On Windows
the backslash escape is unavailable (`\` is a path separator there), but
`[[]` matches a literal opening bracket. A closing bracket is already
literal outside a character class; do not replace it with the invalid
pattern `[]]`. Our earlier claim that neither bracket could be matched
literally was wrong. Actual restic 0.19.1 tests cover these patterns,
including unpaired brackets, Unicode and similarly named siblings.

Every Backspace restore writes into an isolated staging directory first.
Only validated requested paths
are published to the target after a successful or explicitly partial
terminal result. Unrelated existing target files are never swept.
Incomplete staged files are not published. Target junctions and linked
ancestors are rejected before staging. Publication moves completed files
one at a time: if cancellation or a disk error occurs during that phase,
already copied files remain and the result reports the copied count. There
is no transaction or rollback spanning all restored files.

The [Backrest Windows bracket fix](https://github.com/garethgeorge/backrest/pull/1254)
was merged on June 30, 2026 (status rechecked October 12). It is included in
our bundled v1.14.1. Backspace passes the original literal path to native
Restore and lets Backrest escape it. That release still substitutes
wildcards, so it can read extra matching files into staging. The exact
selection check prevents those files from reaching the user's target.
Direct restic restores can use the literal-bracket pattern described above.
The [follow-up exact-match fix](https://github.com/garethgeorge/backrest/pull/1411)
was submitted on 12 October with real-restic regression tests. It is not
part of bundled Backrest 1.14.1; keep that distinction when describing
which engine protection the app currently relies on.
Backspace uses native Restore for a single selected file or folder when no
per-backup bandwidth limit is set. Its target must not exist and its plan ID
must be supplied. The output is still staged and filtered before publication;
native progress comes from the operation's structured status. Scattered
selections, per-backup bandwidth limits and significant whitespace use one
owned bundled restic process with repository credentials read from Backrest.
Its output is consumed continuously: progress is forwarded and discarded,
while only the latest summary and a bounded diagnostic tail are retained.
These restores appear in Backspace, but not in Backrest's operation history.
Native Restore cannot accept several paths or a per-request bandwidth limit.
Both paths keep staging and the repository lease until engine exit is confirmed.
The proposed [restic literal include flag](https://github.com/restic/restic/pull/21872)
was still open, awaiting review, when checked on October 12, 2026. The bundled
restic v0.19.1 does not have that proposed flag. It is not required to match
brackets literally. Keep staging and target-preservation checks when changing
engine versions or pattern handling; they also protect against partial output,
unsafe paths and an engine that does not honor the requested selection.

### Reviewing the upstream bracket fix

A merge of Backrest PR #1411 does not change the binary already bundled
with Backspace. First identify a stable release containing the fix, then
verify that the version actually bundled and run by our tests includes it.
The follow-up date and review results belong to Review desk **R007**, not
a second schedule or status list in this document.

The native restore path already passes literal names to Backrest; Backspace's
own native bracket substitution was removed during the 1.14.1 upgrade.
The upstream fix should stop extra, similarly named files from being read
into staging. It does not by itself unlock a large deletion of local code.
Review any remaining compatibility branches and the adjustment that reports
progress for the files actually copied to the destination. Remove them only
if tests show they no longer serve another purpose. Update obsolete wildcard
explanations when the bundled engine no longer has that behavior.

Keep the direct-restic bracket escaping: those restores bypass Backrest, so
its fix cannot replace that helper. Also keep staging, exact selection and
path checks, overwrite rules, completed-file publication, and cancellation
ownership. These protect against failures beyond bracket matching.

Before simplifying anything, run real-engine bracket-file and bracket-folder
restores beside deceptive sibling names, both with empty targets and existing
files. Check cancellation and both native and direct restore paths. E40–E42
cover these cases; the restore safety regressions and E48 upgrade rehearsal
cover the surrounding boundaries. Retain the negative assertions: unrelated
files must stay absent or unchanged. Attach the results to R007 before
claiming a workaround is safe to remove.

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
