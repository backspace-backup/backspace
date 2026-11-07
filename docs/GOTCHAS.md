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
an interactive desktop are not a dependable unattended destination. The
background service reaches a share only by its network path with the user
name and password saved for the destination (ServiceNetworkShares, U071).

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

Files written by the service take their folder's permissions. Restricting a
file to "the current user" means Windows itself under the service, which shuts
out the owner and Dropbox: the engine's sign-in file did this until 1.1.2
("give Dropbox permission to access it", 4 November 2026). Backspace changes
no permissions; it replaces such a file instead (`BackrestSidecar.AuthFileIsShutOff`).

Explicit Quit pauses scheduled work for the current sign-in session only.
Closing to the tray leaves schedules running. Reopening the GUI, signing out or
restarting ends that pause; locking the desktop, sleep and hibernation do not.
The session record uses both the session ID and its Windows logon timestamp.
The service learns of the sign-out from Windows' session-change notification;
a restart or a vanished session also ends the pause. The implementation must
never replace this with stopping or disabling the service, because a stopped
service cannot resume itself at sign-out, and runs before sign-in after a
restart are requested.

## Saved passwords that travel (U153)

`vault.bin` sits in the synced folder; anything else that holds a credential
in readable form must not. Backrest writes every repository password into its
own `config.json` (by design), and rclone keeps cloud tokens in its
configuration. Those are kept in `%LOCALAPPDATA%\Backspace\engine\<16 hex>`
(`EngineWorkPaths`) and rebuilt there. A new feature that writes an engine
file, scratch file or diagnostic under `AppPaths.ConfigRoot` can silently
undo this: E63 scans every byte under the configuration folder for the
storage password after a real backup, with a positive control in the
engine's folder. Run history and transcripts stay in `Backspace.config\logs`
and `service\logs` (U138); they must never carry a credential.

Two computers that save credentials at the same time each rewrite `vault.bin`;
Dropbox keeps one and makes a "conflicted copy" of the other. Backspace never
merges conflicted copies by itself. Both share the vault id and key, so either
copy opens with the same password; the person can compare them. Saving a
credential on one computer at a time avoids it.

The store never asks for a password; only `VaultGate` does, and only before
an action that needs one. Do not call `SecretsStore.Set` from a path that has
not passed the gate: it throws `VaultLockedException` (nothing is written)
rather than prompting. Background runs never ask: a locked vault makes
scheduled runs wait with one alert, and the service's runs fail before any
engine starts with "Open Backspace on this computer and enter your password once."

A key copy is protected by Windows for one account on one computer (DPAPI).
Restoring `%LOCALAPPDATA%` from another computer gives copies that do not
open: the vault then counts as locked and asks for the password once. A key
copy that opens but no longer opens the body (the vault was replaced, for
example by starting over elsewhere) is deleted as stale. A wrong password is
never written anywhere, and a damaged body, a newer format or a missing file
with a `.bak` beside it are kept as they are.

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
The current pins are Backrest 1.14.1 and restic 0.19.1; on Windows, restic is
Backspace's build of 0.19.1 with a faster exclusion matcher
(`resticBackspace`, [engine/restic/README.md](../engine/restic/README.md)): an
upgrade rebases its patch and builds a new one. Do not replace
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

Backrest (1.14.1) also resets its task queue when it detects a clock jump
(the computer waking from sleep) or applies a settings change. Queued tasks
that had not started are removed and their pending operations deleted, but
their callbacks are never called, so a `Backup` request waiting on such a
task never answers. Backspace treats an operation that disappears for 15
seconds, or a `Backup` whose queued operation never appears within 60
seconds, as discarded (`EngineTaskDroppedException`); nothing is running,
so the run's ordinary retry submits it again (U093). Found when the PC
hibernated during the two-cloud test and the test waited for hours.

A status poll that goes unanswered is not, by itself, a stopped task: while
restic scans a whole drive, Backrest can answer slowly for minutes, and
counting 30 s of slow polls as an uncertainty cancelled a working backup
(U111). Unanswered polls start the uncertainty countdown only after a stop or
once the request has ended. Patience still has limits
(`ResticBackrestEngine.EngineSilenceLimit`, 10 minutes of no answer at all,
and `ProgressStallLimit`, 60 minutes of a running backup whose counts don't
move): the task is cancelled and the run fails with
`EngineStalledException`, which is non-retriable so a stuck network doesn't
cost another hour at once. If cancellation can't be confirmed, the usual
fail-closed block applies.

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

## A stopped Windows service and Fast Startup

"Shut down" with Fast Startup (the Windows default) hibernates Windows' own
session instead of ending it, so services come back as they were: a service
stopped by Quit stays stopped, and automatic start does not apply. The
background service therefore also starts on Winlogon and Kernel-Boot events
(`SystemServiceTriggers`). They fire after a full start and after a resume
from hibernation, and also at shutdown and on lock and unlock; the service
then decides from `service\quit.json` whether the owner's Quit still holds (the
same sign-in session in the same boot) and stops again within a fraction of a
second if so (`SystemServiceLifetime`). Because those starts often coincide
with the owner opening Backspace, the service reads the record with delete
sharing, and the desktop retries deleting it as long as it takes (see below): Dropbox
also opens it when the portable folder is synced (U121, 4 November 2026: "Scheduled tasks
are still paused … being used by another process"). When the service stops, it
keeps reading its backup worker's output until the worker exits: a stopping
worker still writes its last progress and run record, and with nobody reading,
its pipe filled and it hung until the service killed it after 15 seconds (U126:
Quit took 15 s and the run's final report was lost). Measured in the lab VM on 2 November
2026, which could not produce a true Fast Startup start (it has no hibernation
file and falls back to a full start). Do not use Microsoft-Windows-Kernel-Power
or the User Profile Service as triggers: they fire thousands of times or every
two minutes. `ServiceController.Stop` also lists dependent services; the owner
may only stop and query, so `SystemService.StopAndWait` calls `ControlService`
directly.

## Files another program holds (Dropbox, virus scanners)

Backspace.config sits beside the executable, which is often inside a Dropbox
folder. Dropbox, virus scanners, the search indexer and the other Backspace
process (app or service) open files there briefly, so any read, write, move or
delete can fail with a sharing violation (Windows errors 32 and 33), "access
denied" (5) or 1175. The rule for every persistent file: retry until it works,
for as long as it takes, never give up after a fixed time, and never treat a
locked file as missing, empty or corrupt (U135, 4 November 2026).

- `FileAccessRetry.Run` / `Get` / `RunAsync` retry forever. In the app, a
  notice appears after a few seconds and lets the person stop that one
  operation. In the background service, which has no window, the retry logs
  a warning and ends only with the operation's stop token. Call it from a
  worker thread, not the UI thread.
- `StateFileLock.Acquire` without a timeout waits as long as it takes.
- `ServiceSetup.ReplaceRetrying` and `ServiceNotificationQueue.WriteRecord`
  go through the same retry. Write records to a fresh temporary name (a fixed
  `.tmp` name can itself be held), then move it over the record.
- Never retry Windows errors 1176 and 1177 from `File.Replace`: Windows may
  already have renamed the original (`FileReplacementErrors.CanRetry`).
- The service reads `quit.json` in `OnStart`, inside the 30 seconds Windows
  allows for starting: while the file is held it asks Windows for more time
  (`RequestAdditionalTime`) instead of guessing.
- Readers share for writing and deletion where they can
  (`FileShare.ReadWrite | FileShare.Delete`), so the other process can still
  replace the file.

Known gap: `ConfigStore` saves while holding its in-memory gate, so a long
lock during a save also delays code that reads the settings meanwhile.

## Filters are restic's exclude file (U151)

A backup's Filters text goes to restic unchanged, as a file
(`EngineWorkPaths.Root\filters\<plan id>.txt`), through the plan's
`backup_flags`: `--iexclude-file` on Windows, `--exclude-file` elsewhere. Not
as `excludes`/`iexcludes` arguments, which Backrest passes as `--iexclude X`:
restic expands `$NAME` (and `${NAME}`) in an exclude file but not in an
argument, so the same text would mean two things. Backrest splits each
`backup_flags` element like a shell (`shlex`), so the path is single-quoted
(`QuoteArg`). The file is rewritten before every use of the plan; the plan's
flags only change when the path does.

Traps, each checked with restic 0.19.1:

- **`$` in a rule.** `?:/$WinREAgent/` expands to `?://` when the variable is
  unset: the whole drive. Literal dollars are written `$$`; the recommended
  lists and the Duplicacy conversion do so.
- **The drive.** On Windows restic matches `C:/Users/…`. A rule starting with a
  plain `/` matches nothing there; `?:/X` is X at the top of any drive (only a
  drive name holds `:`). An unanchored rule's first `*` can match `C:`, so
  `*/**/X/*` also matches `C:/X/…`.
- **Anywhere means anywhere.** An unanchored rule also matches the backed-up
  folder and the folders above it: `skip` takes all of `D:\skip\tree` (E30).
  Recommended rules that would leave out a whole backed-up folder are not
  offered for it (`RecommendedFilters.AppliesTo`): tests run in a folder inside
  the profile's Temp, which `?:/Users/*/AppData/Local/Temp/` covers.
- **A trailing `/`** is cleaned off by restic: `Cache/` also excludes a file
  named Cache. Restic cannot tell files from folders.
- **An invalid rule** (an unclosed `[`) makes restic refuse the whole file and
  the backup fails. The editor names such rules (`FilterTranslator.InvalidRules`).

Count, Simulate, the size estimates, the change listing and test-restore
completeness read the text with `FilterTranslator` on full paths, restic's
semantics. A rule with two or more `**` is matched with restic's own steps
(`FilterTranslator.ResticMatch`), so it leaves in exactly what restic leaves
in: with the fixed loop bound on Windows, where Backspace's restic carries
the upstream fix (`engine/restic/patches/0002`), and official restic's bound
elsewhere (below). On Windows a rule starting with
`/` matches nothing in restic (a path starts with its drive); the editor
lists such rules (`RulesMatchingNothing`) without changing them. The
environment is this process's; Backrest's restic inherits the daemon's, which
for the background service is the service's own. E57 compares both with
`restic backup --dry-run` on the same file. On 6 November 2026 the owner's
converted lists, on real folders, kept the same files in Simulate and in
restic (local_c 26,639 plus 17 files Chrome held locked, which restic could
not read; local_d 956; local_dropbox 4,887).

Duplicacy lines are converted once (`DuplicacyFilterConverter`), when the
settings load and at import, and the result is saved. Nothing reads `e:`/`i:`
lines at run time any more; a text that still has them would reach restic as
odd file names, and `[^/]` in them makes restic refuse the file.

## Restic's exclusion matching is slow

Restic tests every exclusion rule against every path, on every run (twice
before Backspace passed `--no-scan`: the counting scanner and the archiver),
and its `**` handling tries
every depth: `match` expands the first `**` into 0…n `*` parts and recurses.
On local_c's 273 rules this was 93% of the time to walk C:\ (330 µs a path;
restic's own `internal/filter` code timed on 743,491 real paths, 4 November
2026). Patterns such as `C:/**/*.tmp` cost the most.

On Windows the fix is Backspace's restic build
([engine/restic/README.md](../engine/restic/README.md)), which skips a rule when
the path lacks text the rule needs, inside restic and with restic's own result.
Backspace passes the rules to restic as written. A rule-list rewrite (1.1.10
only) was removed in 1.1.11: with the patched build it saved little, and restic
should see the user's own rules. Two traps for anyone tempted to rewrite rules
again:

- `Distinct()` is wrong with `!` rules. Restic's last matching rule decides, so
  `a`, `!b`, `a` must keep the last `a` (`WithoutRepeats`), not the first.
- "Obvious" simplifications are wrong at the edges: official restic's loop
  bound `i <= len(strs)-len(parts)+1` counts each later `**` as one component
  (restic issue 22131, fixed by pull request 22134 and in Backspace's Windows
  restic, whose bound adds one per `**`), so with official restic
  `C:/**/*/**/node_modules/*` does **not** match `C:\a\node_modules\x` (but
  does match `C:\a\node_modules\x\y`).

## Restic's walk: why `--no-scan`, and why not the folder listing's metadata

Profiled on C:\ with local_c's rules (5 November 2026), 60% of the walk's CPU
was `os.Lstat` (`GetFileAttributesEx`): restic's counting scanner and its
archiver each look up every file and folder. Backspace passes `--no-scan`
(`BuildBackupFlags`): the scanner only feeds the totals and percentage of
restic's status, never the snapshot. Restic then reports 0 for
`total_files`, `total_bytes` and `percent_done`; `BackupLiveProgress`
treats both totals at 0 as "no totals" and uses the `ChangePlanner` listing
instead (shown with "~"). If that listing fails or takes over three minutes,
the line shows the bytes and files done against the expected size, with no
time left.

The remaining look-up per entry stays. Windows does return size and times in
the folder listing (`FindFirstFile` data), but for a file kept open for
writing that copy is stale: tested on NTFS, a 1,000-byte file grown to
3,001,000 bytes by a handle still open listed as 1,000 bytes with its old
date, while `GetFileAttributesEx` gave the new size and date. Restic decides
"unchanged" from size and date, so using the listing would skip changed files
that stay open (mail stores, databases, virtual disks) for as long as they
stay open. Backspace's own listing (`ChangePlan`) uses that data only for the
progress estimate.

### Where a backup's time goes now (for future speed work)

Measured on the owner's PC, backing up C:\ with local_c's 273 rules, little
changed since the previous backup, 5–6 November 2026, Backspace's patched
restic build:

| Stage | Before | Now | What did it |
|---|---|---|---|
| Exclusion matching | 93% of the walk (330 µs a path) | small | Rule pre-check in Backspace's restic build (section above) |
| Restic's CPU for the walk | 252 s | 110 s | `--no-scan`: one walk instead of two |
| Walk time (wall clock) | 124 s | 102 s | same |
| Look-up of each file and folder (`os.Lstat`, `GetFileAttributesEx`) | 60% of the 252 s CPU (two look-ups per entry) | 53.5 s of the 110 s CPU (49%) | Kept, see above |

The look-up is now the largest single cost of a backup where little changed.
Uploads are separate: they depend on how much changed and on the destination.

Options already measured, so nobody repeats them:

- **The folder listing's size and date instead of the look-up:** would take
  the walk from about 102 s to 50–55 s, but misses changes to files held open
  for writing (above). Rejected.
- **`GetFileInformationByName`** (Windows 11 24H2), a cheaper exact look-up:
  exact, but no faster: 15.4 s against 15.8 s on the 266,602 entries of
  `C:\Windows\WinSxS`.
- **Telling which files another program holds open for writing,** to use the
  listing for the rest: Windows has no cheap way to ask.

The remaining large lever is the **NTFS change journal**, which lists exactly
the files changed since the previous backup, so unchanged files need no
look-up at all. It means a substantial change to restic of Backspace's own:
the journal position for each drive, a full walk whenever the journal was
reset or overflowed, Windows only, and unlikely to be accepted upstream. A
mistake in it would silently skip changed files, so it needs its own design
and the owner's go-ahead before any work.

## An interrupted backup leaves data behind until a prune

Restic writes its index (the list of what it stored) every 10 minutes or at the
end. A backup stopped before that leaves data packs no index lists; the resumed
backup sends that data again, and `restic check` reports "additional files ...
likely contain duplicate data". Only `restic prune` deletes them (local_c had 652
such files after several interruptions). The cleanup after each backup therefore
runs `forget … --prune`, and a full `prune` after a backup whose previous run did
not finish, or when Backrest's history shows no successful prune for 7 days.
Before 1.1.10, cleanup ran `forget` alone, so storage never shrank.
