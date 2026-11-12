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
With the service set up it runs every backup (U171), so a share without them
fails its run with what to enter; it is not handed back to the window.

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
no permissions; it replaces such a file instead.

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
in readable form must not. rclone keeps cloud tokens in its configuration;
it is kept in `%LOCALAPPDATA%\Backspace\engine\<16 hex>` (`EngineWorkPaths`)
and rebuilt there. Repository passwords reach restic only in its environment
(U175). A new feature
that writes an engine file, scratch file or diagnostic under
`AppPaths.ConfigRoot` can silently undo this: E63 scans every byte under the
configuration folder and the engine folder for the storage password after a
real backup and a listing, with a positive control. Run history and transcripts stay in `Backspace.config\logs`
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

A `secrets.bin` left in `Backspace.config` or `service\` by a version from
before the vault is ignored: nothing reads it, and its presence does not make
the vault count as locked. The one-time move from it has been removed (the
owner is the only user). The saved cloud review sign-ins under
`audit-dashboard/private/temp/BackspaceReviewTests` are still in that format;
only the test helper `ReviewSignInFile` reads them.

## OAuth first-run flow (Dropbox / OneDrive / Google Drive)

A run can end with a failed result (restic's exit code and its words) instead
of throwing an exception. Keep its explanation in the run's saved error list
for backup and maintenance. The backup orchestrator must classify those returned failures
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
limit and runs outside the repository's turn. Classify only
its stderr diagnostics; directory names from stdout must never be treated as
provider error messages.

Generated Dropbox remotes set `batch_mode = off`. rclone's default batching
holds each upload for a 0.5-second idle window, then commits it in a separate
request. Restic writes its lock, index and snapshot files one at a time, so
batching added about 10 seconds to every backup in October 2026 measurements,
without making large uploads faster. Each restic command on Dropbox still pays
several seconds of fixed round trips (starting rclone, reading the key,
locking). Before a backup, one
`restic snapshots --json --no-lock <id>` reads the remembered previous snapshot
(no lock, no list of the others), nothing runs afterwards, and `restic cat
config` runs only the first time this computer uses the repository. These extra
commands are most of the remaining difference from tools that make a single
connection.

A portable install does not put rclone on the system PATH. Every restic
Backspace starts (`ResticRunner`, the restores and listings, the password
change) gets the absolute bundled-engine directory first on its PATH
(`BundledEngineEnvironment`), so restic finds the bundled rclone. Do not put
the executable path into repository options instead: paths with spaces,
backslashes or punctuation must reach restic as one argument each, which
`ProcessStartInfo.ArgumentList` guarantees.

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
The current pins are restic 0.19.1 and rclone 1.74.1; on Windows, restic is
Backspace's build of 0.19.1 with a faster exclusion matcher
(`resticBackspace`, [engine/restic/README.md](../engine/restic/README.md)): an
upgrade rebases its patch and builds a new one. Do not replace
those tags with `latest`: a successful backup alone does not establish
compatibility. File-tree listing starts at the selected source's absolute
snapshot path. Starting at `/` would open another restic/rclone session for
every source ancestor before reaching any files; on cloud repositories, this
can add several minutes. Keep the source-relative output and immutable
snapshot selection when changing this traversal.
Existing local binaries are not overwritten by the downloader, so an upgrade
must replace them deliberately and verify the versions in the test output
directory as well as in the source assets. Run real backup, restore,
cancellation and maintenance checks against those binaries.

Since U175 Backspace reads restic's output itself, so a restic upgrade must
also pass `ResticRunnerTests`, `DirectBackupTests` and
`DirectReadAndRestoreTests` with the new binary:
the JSON messages (`status`, `summary`, `error`, `exit_error`), the exit codes
(3, 10, 11, 12, 130), the "signal interrupt received, cleaning up" line after
Ctrl+Break, and `snapshots --json <id>` answering a missing or unreadable
snapshot with `[]` and exit code 0 (the remembered parent's check relies on
it).

## Repository setup contract

A repository this computer has not used yet is opened with `restic cat
config`; only when restic says there is none (exit code 10) does
`restic init` create one. A password that does not open the repository (exit
code 12) is "this folder already holds another backup" (U045), never a new
repository beside it. The repositories this computer has opened or created
are in repositories.json in its engine folder (`KnownRepositories`), with
restic's own id for each. `CompletedEngineFailureTests` checks the
wrong-password message after a direct backup.

An interrupted repository initialization can leave encryption keys without a
usable repository configuration. If `restic init` ends with the closing
message (`exit_error`) `repository already contains keys`, Backspace stops
after one attempt.
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
never use it to reuse bytes from an earlier mount. Files reach the cache only
once complete: a download in progress lives in `partial/` and is renamed into
`files/` after its size matches the snapshot's.

Never make a WinFsp read wait for a whole file (U167). The first copy out of
the drive used to wait inside one read for a full `restic restore`: WinFsp
cancels a request unanswered after its IRP timeout (5 minutes by default), so
a large file on a slow cloud failed to copy while the restore still finished
into the cache, and the second copy succeeded. Worse, WinFsp opens a file
only when none of its reads is in progress (create takes the file's lock
exclusively, a cached read holds it shared), so everything that asked about
that file, Explorer's window included, hung for the whole download. Reads are
now answered per range from a progressive `restic dump`; opening the file
being copied still waits for the copy's read in progress (measured: median
0.3 s, at most 1.7 s at 40 MB/s), and its very first read waits for restic to
find the file. A read far ahead of the download (a player seeking to the end
of a film) waits until the download gets there: `restic dump` has no ranges.
Snapshot listing and restore processes retain their repository lease
until process exit is confirmed; a cancelled HTTP wait alone is not sufficient.

Never ask the engine for a mounted snapshot one folder at a time (U158). Each
folder cost a restic process (repository index load), and on a cloud
destination also a separate sign-in check, so every folder of a
large cloud backup took seconds. The mount reads the whole tree with
`restic ls --recursive` (shared out between processes by top-level folder)
before the drive appears and answers from memory; the per-folder call remains
only for an engine that cannot list a whole tree. Giving each process many
second-level folders instead was measured slower. A listing's standard output
must not go through `StreamingRestoreProcess`'s diagnostics (each line parsed
and kept), which cost seconds on 600,000 lines.
Equally, never restore a whole snapshot because a few files were opened: the
former "two opens fetch everything" rule began restoring all 600,000 files of
a measured fixture after the second file. Only files that are read are
fetched; nothing is prefetched. WinFsp's cache
timeouts are infinite on this read-only volume; keep them so, or Explorer asks
again every second.

A mounted snapshot and every restore hold their destination through
`SnapshotReaders` (U159): cleanup, snapshot removal, prune and password changes
check it in the engine, so the background service obeys the window's mount.
Restic's own repository lock does not cover this: listings run with
`--no-lock`, and a mount reads only now and then, so a prune between two reads
could remove what the drive shows.
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

Every restore runs one owned bundled restic process (U175):
`restore <id>:/<source>`, the selection in
an include file with each opening bracket escaped, none for the whole source,
and the backup's download limit among the repository's options. The output is
staged and filtered before publication. It is consumed continuously: progress
is forwarded and discarded, while only the latest summary and a bounded
diagnostic tail are retained. Staging and the repository lease are kept until
restic's exit is confirmed.
The proposed [restic literal include flag](https://github.com/restic/restic/pull/21872)
was still open, awaiting review, when checked on October 12, 2026. The bundled
restic v0.19.1 does not have that proposed flag. It is not required to match
brackets literally. Keep staging and target-preservation checks when changing
engine versions or pattern handling; they also protect against partial output,
unsafe paths and an engine that does not honor the requested selection.

Before changing pattern handling or the bundled restic, run real-engine
bracket-file and bracket-folder restores beside deceptive sibling names, both
with empty targets and existing files, and with cancellation. E40–E42 and
`DirectReadAndRestoreTests` cover these cases. Retain the negative
assertions: unrelated files must stay absent or unchanged.

## Repository command lifetime and cancellation

Every restic run, a backup's or a restore's, returns only once restic has
exited: the caller's repository lease and a restore's staging folder stay
owned until then, including a kill that cannot be confirmed (that case blocks
further repository work until restart rather than unlocking maintenance over
a possibly live writer). A backup whose counts don't move for
`ProgressStallLimit` (60 minutes) is stopped and fails with
`EngineStalledException`, which is non-retriable so a stuck network doesn't
cost another hour at once (U111).
A direct command stops in two steps (`ResticRunner.StopAsync`): Ctrl+Break to
restic's own process group, then, after `GentleStopWait` (3 s), the end of its
whole job, rclone included, and a wait for the exit. restic is started in a new
process group so the break reaches it and not Backspace; restic starts rclone
in a group of its own, so rclone does not get it. On the break restic logs
"signal interrupt received, cleaning up", removes its lock and exits with 130.
A backup held up by `--limit-upload` may not exit within the 3 s and is ended,
its lock removed already on a local repository; a lock left through rclone is
stale (its process is gone) and goes at the next command's one `restic unlock`.
The window and the service's worker are Windows applications without a console:
to send the break, `ConsoleBreak` attaches to restic's console, then detaches.
A test host has a console and shares it with restic. The guard handler that
ignores Ctrl+Break in Backspace itself must stay installed: without it, a
break that reaches Backspace would end it. On macOS and Linux the gentle step
is SIGINT through the system's `kill -INT` (restic handles it as Ctrl+C);
that path compiles but has not been exercised on either OS. Restores, listings
and file reads (`StreamingRestoreProcess`) are ended without a gentle step: a
listing and a file read take no lock, and a stopped restore's lock is stale (its
process is gone), removed by the next cleanup's one `restic unlock`.

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

## Online-only files and `--exclude-cloud-files` (U168)

Official restic's `--exclude-cloud-files` skips only files marked
`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x400000). Most processes see Cloud
Files placeholders disguised: a Dropbox online-only file reads 0x1620
(ARCHIVE | SPARSE_FILE | REPARSE_POINT | OFFLINE), Go calls it an irregular
file, and restic 0.19.1 reported `unsupported file type "irregular"` for each
one and ended the run "Partial backup" (STATUS_WARNING). Measured 9 November
2026: 58,335 such files in the owner's Dropbox; 8 errors for the 8 placeholders
of one folder. Nothing was downloaded.

Backspace's Windows restic (patch 0003, `engine/restic/README.md`) also treats
a file marked OFFLINE or `FILE_ATTRIBUTE_RECALL_ON_OPEN` as online-only, from
the attributes Lstat already read, in the select step before the file's type is
looked at: such files are skipped without an error and without opening their
data. `CloudFileBehavior.IsCloudOnly(FileAttributes)` is the same rule, used by
Left out, the estimates and the run's own listing. Keep the two equal.

- The reparse tag is not used. A placeholder whose data was downloaded keeps
  its Cloud Files tag, and its data is local: skipping it would lose data.
- Folders count only with RECALL_ON_DATA_ACCESS, as in official restic.
- Restic does not count what it skips. The run's listing of the source
  (`ChangePlanner`, U144) counts the online-only files its rules keep, and the
  run summary ends "N online-only files skipped". When that listing does not
  finish (three minutes), the run says nothing about them.
- Placeholders are reparse points, so `TreeWalk` hands them to its link
  callback, not the visitor: count them there too.
- With the option off nothing changes: restic opens online-only files, which
  can download them, and the Dropbox ones still end as irregular-file errors.
- On Windows ARM64 Backspace uses official restic, which reports Dropbox
  placeholders as errors whatever the option; the run does not count them as
  skipped there.

## Filters are restic's exclude file (U151)

A backup's Filters text goes to restic unchanged, as a file
(`EngineWorkPaths.Root\filters\<plan id>.txt`), on the backup's command
line: `--iexclude-file` on Windows, `--exclude-file` elsewhere. Not
as `--iexclude X` arguments: restic expands `$NAME` (and `${NAME}`) in an
exclude file but not in an argument, so the same text would mean two things.
The file is rewritten before every backup, only when its text changed.

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
- **Group headings are data.** The recommended block is read back by its
  comment headings (`RecommendedFilters.ReadChoice`, `CheckForUpdates`): a
  group's heading present means the group is ticked, even when only
  `# None of these apply to this backup's folders.` follows it. Changing a
  heading's text needs a conversion of saved texts, as U165 did for the
  Essentials (`UpgradeEssentials`, old headings kept only there). A rendered
  group lists only rules that can match in the backup's folders; a block
  rendered without sources (every rule) for a folder backup offers the
  drive-top ones for removal.
- **The Essentials can be unticked.** Windows' app execution aliases
  (`?:/Users/*/AppData/Local/Microsoft/WindowsApps/`) are in that group; with
  it unticked, restic reports each alias as an unsupported file type and a
  backup of a profile ends as a partial backup (STATUS_WARNING).

Left out (`LeftOutAnalyzer`), the size estimates, the change listing and test-restore
completeness read the text with `FilterTranslator` on full paths, restic's
semantics. A rule with two or more `**` is matched with restic's own steps
(`FilterTranslator.ResticMatch`), so it leaves in exactly what restic leaves
in: with the fixed loop bound on Windows, where Backspace's restic carries
the upstream fix (`engine/restic/patches/0002`), and official restic's bound
elsewhere (below). On Windows a rule starting with
`/` matches nothing in restic (a path starts with its drive); Left out
lists such rules (`RulesMatchingNothing`) without changing them. The
environment is this process's; the background service's restic gets the
service's own. E57 compares both with
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
not finish, or when the repository has had no successful full prune for 7 days.
Before 1.1.10, cleanup ran `forget` alone, so storage never shrank.

Both answers come from Backspace's own records since U175 (maintenance.json,
`MaintenanceRecords`): a plan whose `lastBackupStartedUtc` is later than its
`lastBackupFinishedUtc` did not finish, and `lastFullPruneUtc` is written by a
successful full prune after a backup or on the cleanup schedule. A repository
with no `lastFullPruneUtc` gets a full prune at its next cleanup after a backup.

## Retention: Duplicacy rules on restic (U172)

The KeepPolicy keeps Duplicacy's `n:m` syntax, applied by Backspace's own
`forget` after each backup (or on the cleanup schedule) with restic's
keep-within options (`KeepPolicyRules.cs`). Before 9 November 2026 any `n:m` policy
became a fixed `--keep-last=10 --keep-daily=7 --keep-weekly=4 --keep-monthly=12
--keep-yearly=5`, whatever the rules said. Things to know before changing it:

- Only Backspace's forget removes snapshots. Never add a second forget
  with other rules (another tool, a second schedule): two forgets with
  different rules would delete the union.
- `--keep-within*` durations count back from the **latest snapshot** in the
  group, not from now (restic's documented behaviour). Duplicacy counts from
  now. So restic never deletes more for age reasons, and deletes nothing more
  once backups stop.
- Restic keeps the last snapshot of each calendar period; Duplicacy keeps a
  snapshot when it is n days (less 10 minutes) after the previous kept one,
  starting from the oldest. Different snapshots survive. On a synthetic year
  of daily backups (throwaway repository, Backspace's restic), the owner's
  three policies kept 116/117, 55/55 and 65/67 snapshots (Duplicacy/restic),
  the same in every band up to 90 days; a 180–365 day band of monthly
  thinning kept 7 with Duplicacy and 6 with restic, with at most 31 days
  between kept snapshots instead of 30. An n that is not 1, 7, 30 or 365 uses
  the next shorter calendar period, so it keeps more.
- A policy that does not parse returns no options, and no options means no
  forget at all: restic refuses a forget with nothing to keep, and Backspace
  never sends one.
