# GOTCHAS.md — known sharp edges

Upstream-driven traps and platform-specific surprises that aren't
obvious from the code. Read before touching the related subsystem.

## Unattended Windows access

Earlier builds registered tasks as the current Windows account using
S4U, with the highest privileges that account could use. Current builds
register no Windows task for backups: the tray app keeps the schedule while the
owner is signed in, and removes the owner's earlier tasks when it starts.
Running while signed out is the background service's job. Windows could launch
the old S4U tasks while the account was signed out, without saving its password.
That does not give them access to every user's files. Local permissions,
locked drives and EFS encryption still matter; mapped network drives from
an interactive desktop are not a dependable unattended destination.

Microsoft documents that S4U tasks have no access to the network or encrypted
files. In particular, do not promise access to a Windows-authenticated network
share or to EFS-encrypted source files. Cloud destinations use separate OAuth
credentials, but their availability also depends on the encrypted local vault,
token refresh and the run's actual Windows context. A successful cloud backup
from the GUI is not evidence that it works before sign-in. Test each intended
unattended configuration; those live-account and reboot checks remain separate.
See [Microsoft's logon-type contract](https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-logontype).

Treat an S4U credential read as potentially destructive, not merely unavailable.
On 18 October 2026, two isolated Windows 11 build 26300.9711 trials showed
`CredReadW` returning "not found" while LSASS deleted valid local and roaming
Credential Manager files. Password-authenticated reads still failed afterward.
The normal-reboot, no-read S4U and DPAPI-only controls preserved those files.
This does not establish the cause of a particular real-machine incident, or
whether it is the same defect as the historical S4U problem fixed in 2021.
The evidence and Microsoft report are recorded in the local Review desk's N046.
Do not test this failure path on a real user profile.

A successful password-free repository backup cannot establish access to the
encrypted secrets vault. A failed S4U vault read also does not mean the user
copied the configuration from another account: the same account can have a
different ability to decrypt data in a different logon session. Preserve the
unreadable vault and diagnose the logon context before asking the user to
replace credentials. Before-sign-in and credential-integrity acceptance must
follow the separate requirements in [RELEASING.md](RELEASING.md).

Explicit Quit pauses scheduled work for the current sign-in session only.
Closing to the tray leaves schedules running. Reopening the GUI, signing out or
restarting ends that pause; locking the desktop, sleep and hibernation do not.
The session record uses both the session ID and its Windows logon timestamp.
The service learns of the sign-out from Windows' session-change notification;
a restart or a vanished session also ends the pause. The implementation must
never replace this with stopping or disabling the service, because a stopped
service cannot resume itself at sign-out, and runs before sign-in after a
restart are requested.

## OAuth first-run flow (Dropbox / OneDrive / Google Drive)

Backrest can return a completed failed operation instead of throwing an RPC
exception. Keep its explanation in the run's saved error list for backup and
maintenance. The backup orchestrator must classify those returned failures
the same way it classifies exceptions: known network unavailability is
skipped, explicit rejected credentials fail without another identical attempt,
and unfamiliar errors remain failures. A successful or warning result must
not be reclassified from text in its diagnostics.

Before cloud repository setup and backup/maintenance work, a direct rclone
request lists only directories in the configured cloud root, without recursion
or file downloads. For app-folder connections that root is Backspace's own
app area. Avoid probing a deeply nested repository path: OneDrive can need a
separate slow request for each path component. The direct listing preserves
explicit sign-in errors that rclone's restic server otherwise turns into
generic HTTP 500 responses. Rejected credentials fail with a reconnect
instruction; recognised network unavailability follows the normal skipped
backup policy. A slow probe or a transient server error still proceeds to
restic, so a new repository can be created and ordinary
transfer retries remain enabled. This read-only probe has its own 30-second
limit and runs outside the shared Backrest configuration lock. Classify only
its stderr diagnostics; directory names from stdout must never be treated as
provider error messages.

Generated Dropbox remotes set `batch_mode = off`. rclone's default batching
holds each upload for a 0.5-second idle window, then commits it in a separate
request. Restic writes its lock, index and snapshot files one at a time, so
batching added about 10 seconds to every backup in October 2026 measurements,
without making large uploads faster. Each restic command on Dropbox still pays
several seconds of fixed round trips (starting rclone, reading the key,
locking), and Backrest 1.14.1 runs `restic snapshots` before each backup and
again afterwards to index it. Those extra commands are most of the remaining
difference from tools that make a single connection.

Backrest's working directory is its data directory; a portable install does
not put rclone on the system PATH. Before starting Backrest, prepend the
absolute bundled-engine directory to that child process's PATH. Its restic
children then find the bundled rclone during `AddRepo` and every later
operation. Direct password changes need the same child environment; direct
restores already include the bundled directory. Do not put the executable
path into repository flags: Backrest reparses each flag with shell-style
quoting before restic parses its own options. Paths with spaces, backslashes
or punctuation must survive without relying on those extra parsing layers.

`OAuthFlowCoordinator` runs a PKCE round-trip through a local listener.
Connect receives the token automatically; pasted tokens remain an advanced
fallback. OneDrive uses `http://localhost:<port>/callback` to match Entra's
native localhost registration rules. Dropbox requires the exact registered
`http://localhost:53682/callback`, including its port; a bare localhost
registration does not match. Google uses `127.0.0.1` with an available port.
An occupied Dropbox callback port fails before opening the browser, with
instructions to close the other sign-in. The browser reports success only
after the authorization code has been exchanged for tokens.
Closing the destination editor cancels an in-flight sign-in.

`DropboxAuthProbe` does an `rclone lsd` round-trip against a per-flow
temp `rclone.conf` before save so a stale or scope-mismatched token
fails loudly.

OneDrive instead queries Graph's `special/approot` and persists the app-folder
ID, containing drive ID and actual type before reporting connected. Its remote
is rooted at that folder and requests `Files.ReadWrite.AppFolder` only.
Google requests `drive.appdata`; rclone calls this `drive.appfolder` and uses
`appDataFolder` as its root. Google hides the folder from its normal file list.
Its contents cannot be trashed, so repository maintenance uses direct deletion
of the repository's own objects. All three providers have owner-controlled
official registrations. A captured token still needs a real backup/list/restore
and refresh test. See
[the registration guide](OAUTH_REGISTRATION.md), including Microsoft's
publisher verification requirements for public distribution.

Do not silently upgrade an old cloud destination by changing its root. The
`CloudAppFolderOnly` flag is absent on older configurations and causes an
explicit request to create a new destination. Old data is left untouched.
Changing the Google app registration also changes its private folder; an app
upgrade must preserve the registration that owns existing backups.

Production engine calls use one rclone config per destination, under
`rclone-remotes`. Do not switch these back to the old shared file: rclone
writes refreshed tokens itself. The builder imports a newer on-disk token
into SecretsStore before rendering and preserves a newer interactive
sign-in. Shared-config compatibility writes merge unrelated remote sections
under a cross-process lock. On Windows, atomic replacement retries brief
sharing or access-denied locks for up to two seconds. A persistent lock
still fails the operation and leaves the previous complete file intact.
Do not replace this with an in-place write: a failed save must not truncate
credentials. Live account evidence, including refresh after an application
restart, is recorded separately in the Review desk.

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
File-tree listing starts at the selected source's absolute snapshot path,
then visits only its nested directories. Starting at `/` would open another
restic/rclone session for every source ancestor before reaching any files;
on cloud repositories, this can add several minutes. Keep the source-relative
output and immutable snapshot selection when changing this traversal.
Existing local binaries are not overwritten by the downloader, so an upgrade
must replace them deliberately and verify the versions in the test output
directory as well as in the source assets. Run real backup, restore,
cancellation and maintenance checks against those binaries.

## Backrest 1.13.0+ `SetConfig` guid contract

Plan backup options must use the explicit JSON name `backup_flags`, not
`backupFlags`. The latter can be silently ignored: VSS, cloud-placeholder
exclusion and bandwidth limits then disappear even though a local unit
test sees the right list. The real-daemon configuration test writes and
reads these options through Backrest. Consult the pinned version's proto
for each field; naming is not uniformly camel case.

Guids must be 64-char hex; `auto_initialize=true` is rejected when a
guid is set; the validator reruns over the entire on-disk config on
every write, so an old 36-char UUID-shaped guid (Backspace pre-2026-06)
makes every `SetConfig` fail. Recovery is automatic via the
legacy-guid drop in `EnsurePlanAndRepoCoreAsync`.

A plan written right after `AddRepo` can be saved but not loaded: on a fresh
setup in the lab VM (31 October 2026, U048) the first backup was refused with
"plan not found" on every retry while the plan sat in Backrest's config file,
until another settings write. `BackupPruneCheckAsync` therefore writes
Backrest's settings back unchanged once and retries. The refused request has
started no work. Remove this recovery only after a Backrest release fixes the
race and a fresh-setup first backup passes without it.

## Backrest re-init contract

On first contact for a fresh destination, we send `Guid = ""` +
`AutoInitialize = true` and let Backrest assign the canonical 64-char
guid by adopting the on-disk restic repo (or initialising one).
Subsequent `SetConfig` passes leave the existing repo stanza alone.
Never round-trip our own deterministic guid through `SetConfig` —
restic's actual config-id is the source of truth.

An interrupted repository initialization can leave encryption keys without a
usable repository configuration. If `AddRepo` returns the structured terminal
error `repository already contains keys`, Backspace stops after one attempt.
It preserves the destination and asks the user to keep that folder for recovery
and choose a new empty folder for a new backup. It must not erase keys, recreate
the repository in place, or repeat setup through the normal retry cascade.
Temporary sharing errors and unrecognized output keep their existing retry
behavior. The real-engine regression removes only the configuration of its own
disposable repository, then verifies that every remaining file is unchanged.

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

Scheduled checks need an IANA time-zone name, such as `Pacific/Honolulu`.
Windows normally supplies `Hawaiian Standard Time`; sending that name directly
causes HTTP 400. `HealthcheckService` converts Windows IDs before sending the
schedule and rejects unmappable custom zones rather than silently using UTC.
API v3 accepts grace periods up to 365 days, matching the editor's maximum;
Backspace retains a four-hour minimum allowance for a running backup.
The API key is project-specific. Keep test keys in the isolated encrypted
vault, and assign no integrations to disposable checks to avoid sending alerts.

Management calls log HTTP failures and return an unsuccessful result; they
do not currently retry a batch of check creations. A rejected creation must
not be shown as a connected check. Terminal run pings happen before email
delivery and outside the per-status daily human-alert cap. Use the exact
`ping_url` returned by management/verification for self-hosted services;
a bare legacy UUID still means the public hc-ping.com endpoint.

## macOS / Linux untested

The cross-platform TFM compiles and the platform abstractions
(login-item start, AES-GCM keyfile, `geteuid`) are in place, but
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
The due-date instruction for the next agent is in **AGENTS.md**. Record
the review results in Review desk **R007**; no scheduled automation is used.

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

Test restore offers a byte-budgeted sample or every-file recovery. Both use
bounded batches and include empty files. Source files changed since the
snapshot cannot be called byte-identical; the result labels their weaker
comparison. Invalid relative paths and linked directories must not escape
the scratch directory during comparison or cleanup.

The saved plan lives outside the temporary-file sweep. Never replace its
immutable snapshot ID with the latest snapshot on restart, and never repeat
completed batches merely because a newer backup exists. Changed routes
must stop recovery. Persist an explicit cancellation before stopping the
engine, and preserve the first terminal result when cancellation races with
completion. If saving a stop request fails, say that the check may resume
on the next launch. Sample success is not a full-restore guarantee.

## Backrest's background snapshot index

Backrest 1.14.1 schedules an index after saving a snapshot, but that task has
no pending operation record. An empty pending-operation query therefore does
not establish that the repository is idle. A second reader can overlap the
index and encounter a disappearing lock file on a cloud backend.

Backspace waits for two signals: an increased successful `index_snapshots`
counter at the authenticated `/metrics` endpoint, and an index record for the
exact saved snapshot. Neither signal alone is sufficient. An indexing failure
or loss of confirmation during cancellation keeps further work blocked until
the owned daemon is stopped. A saved snapshot still needs this handling when
cancellation or an end hook changes its final operation status.

On a Backrest upgrade, exercise E54/E55 and the delayed/negative completion
tests against the actual new binary. Do not remove this compatibility handling
until the bundled daemon supplies a tested, cancellable completion record for
its index task. U012 in the local Review desk holds the measured cloud evidence.
