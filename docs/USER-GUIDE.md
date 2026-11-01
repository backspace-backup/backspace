# Backspace user guide

The [README](../README.md) gives the overview. This guide covers how each part
of Backspace behaves, including its limits.

- [Destinations and cloud sign-in](#destinations-and-cloud-sign-in)
- [Schedules, the tray icon and Quit](#schedules-the-tray-icon-and-quit)
- [Administrator access and files in use](#administrator-access-and-files-in-use)
- [Filters](#filters)
- [Restoring files](#restoring-files)
- [Browsing and mounting snapshots](#browsing-and-mounting-snapshots)
- [The test-restore drill](#the-test-restore-drill)
- [Repository checks](#repository-checks)
- [Alerts, email and Healthchecks.io](#alerts-email-and-healthchecksio)
- [Removing a backup](#removing-a-backup)
- [Metered networks](#metered-networks)
- [App updates](#app-updates)
- [Portability](#portability)
- [Importing from Duplicacy](#importing-from-duplicacy)
- [Command line](#command-line)

## Destinations and cloud sign-in

Backspace backs up to a local folder, an external drive, a network share,
Dropbox, OneDrive Personal, Google Drive, or S3-compatible storage (Amazon S3,
Wasabi, Cloudflare R2, MinIO, Backblaze B2 through its S3 interface).

External drives are matched by volume name. A backup whose drive isn't plugged
in is skipped and says so; it doesn't fail.

For cloud storage, choose **Connect** and approve your own account. Backspace
ships its application registrations, so you don't need to create one. Dropbox
and OneDrive use Backspace's own app folder. Google uses a private app-data
folder that is hidden from the Drive file list; browse and restore those
backups in Backspace. Your account tokens stay in your local encrypted vault.
See the [registration guide](OAUTH_REGISTRATION.md) for provider setup and
testing requirements.

## Schedules, the tray icon and Quit

Backspace works like Dropbox and other sync apps. It starts when you sign in
and sits in the tray, and its schedules run while it does. You can turn this
off in **Settings → Startup and tray**.

- **Without administrator access**, backups run on schedule while you are
  signed in and Backspace is in the tray. Before you sign in and after you
  sign out, nothing runs.
- **With administrator access**, Backspace's background service keeps the
  schedule. It starts with Windows, so backups also run before anyone signs
  in. The tray icon appears when you sign in. Backups to an external drive
  run in the service too, whenever the drive is plugged in, and are skipped
  while it is not. Backups to a network share stay with the tray app, which
  runs them while you are signed in: the service cannot sign in to a share.

Closing the window keeps Backspace in the tray, so schedules keep running.
**Quit** from the tray menu stops active work and stops automatic backups
until you open Backspace again, sign out, or restart Windows. Sleep,
hibernation and locking the screen don't end the pause.

Backspace registers no Windows scheduled task for backups. Between runs it
only waits for the next due time, so it uses no CPU while idle. With
administrator access, one Windows task without a schedule remains: Backspace
starts it to apply your saved changes to the background service (see below).

Each schedule has conditions that also apply to its standalone check and
prune:

- **Skip on battery** and **stop on battery**.
- **Require a network connection**, for backups that send data over the
  network. Backups to a local folder or external drive run offline.
- **Catch up missed runs.** A run missed while the computer was off, asleep or
  signed out happens once, as soon as Backspace is running again. With this
  off, a run more than 10 minutes late is skipped.

A run that is due but waiting for power or a network starts as soon as the
condition is met. Runs never overlap: if one is still running when the next
is due, the next is skipped.

On macOS and Linux, Backspace starts as a login item and keeps the schedule
the same way while you are logged in (untested).

## Administrator access and files in use

New backups use Windows snapshots (VSS) by default. VSS includes files other
programs have open and captures everything at one point in time.

VSS needs Backspace's administrator access. Creating a backup includes
**Allow administrator access**, ticked by default; Windows asks you to approve
it when you save, before the first run (a standard account enters an
administrator's password). That installs Backspace's
background service, which runs the portable `Backspace.exe` from wherever you
keep it, so there is no second copy. Backspace doesn't change that folder's
permissions, so where it lives and who may change it is your call. Network
drives can't be used for this.

With access on, **Run now** goes through the same service and includes files
in use without another prompt. Adding, editing or removing a backup later asks
nothing either: Backspace applies your saved settings to the service itself,
a minute later if a backup is running at the time. **Settings →
Administrator access** keeps **Remove access**.

Without access, a backup runs without VSS and says so; no Windows prompt
appears per run. An orange **Files that are open may be skipped** badge at the
bottom of the sidebar explains why and how to fix it.

Turning snapshots off for a backup requires acknowledging the risk of missing
or inconsistent files, and the sidebar shows **Limited protection** until they
are back on. NAS folders and filesystems that VSS doesn't support may need
this exception. A VSS backup can still warn or fail if Windows can't create a
snapshot.

Access to network shares and encrypted source files depends on the account
that runs the backup. See
[unattended Windows access](GOTCHAS.md#unattended-windows-access).

## Filters

Edit restic's filter syntax with a live simulation pane that shows which files
would be included or excluded before you run.

## Restoring files

The restore wizard takes you through it step by step: pick the backup, choose
a moment in time, tick the files you want and choose where they go. You decide
whether to replace existing files and whether to keep their original folder
structure.

You can leave the wizard and return through **Restores**, pause and resume,
or cancel safely while other work continues. Cancel removes files copied by
that attempt and puts replaced originals back. If another program edited a
restored file meanwhile, Backspace keeps both versions and shows where to find
the original. An unexpected process exit during final file placement can leave
recovery copies beside the destination; automatic recovery from that
interruption is not provided yet.

## Browsing and mounting snapshots

**Browse snapshot** on a backup card walks the snapshot's tree inline, without
starting a restore.

**Mount** opens the snapshot as a read-only drive in File Explorer through the
WinFsp filesystem. Open files in your usual apps, compare them with your
current copies, or drag files out. Mounting needs the WinFsp driver: Backspace
checks for it and asks for administrator approval to install it when it's
missing. Backspace itself stays portable, and normal restores don't need the
driver. Backspace removes the drive when you unmount or close the app.

## The test-restore drill

A successful backup run is a claim: bytes were uploaded and the engine
reported success. The test-restore drill turns that claim into a check that
you can get your files back.

Choose **Test restore** on a backup card, then choose the scope:

- **Check a sample** defaults to 1% of the stored file sizes and a 1 GiB
  download budget. It uses the smaller limit, shared across source and
  destination pairs. Files never checked, or checked longest ago, come first;
  ties are random. Whole files are selected, so one large file can exceed the
  budget. These sizes describe recovered files, not exact network traffic:
  compression, caching and repeated destinations affect transfers.
- **Check every file** recovers the latest snapshot of every source at every
  destination, including empty files. Large cloud backups can take hours or
  days. A successful sample never claims this full coverage.

Each check uses the normal restore process. restic validates the data it
reads, and Backspace checks the recovered files and their sizes. Your current
originals aren't needed for those checks.

**Also compare with today's originals** is optional and off by default.
Matching size and modification date make an original eligible for this
byte-for-byte comparison; they don't prove it never changed. A difference
fails the comparison but doesn't establish which copy changed. With every file
selected, this diagnostic also looks for unchanged, included local files that
are missing from the snapshot.

The same check can run automatically: in **Edit → Schedule → Check policy**,
enable **Test recovery from the latest backup** (off by default). It runs
after a successful repository check. This can use substantial bandwidth and
temporary disk space.

**Restores** shows a running check with recovered-byte progress, file counts,
**Pause / Resume** and **Cancel**. Results stay there until dismissed, and
reopening the app loads the last 30 days. The backup card shows the latest
check's age and scope and flags a missed check or an unresolved failure.
**Check history** lists dated results; **History** lists backup runs.

Cancel removes the test's temporary copies. A paused check stays paused when
you reopen Backspace. If the app closes unexpectedly, reopening it resumes the
saved snapshots and sample; completed batches are kept, and an unfinished
batch may be downloaded again. Changed source or destination settings prevent
resuming and show an explanation instead.

Temporary copies are removed after each batch of up to 128 files or 256 MiB;
a larger file is handled alone. Before each batch, Backspace checks for space
for two copies plus 64 MiB of reserve. Original files are never replaced.

## Repository checks

The scheduled repository check, on by default, checks restic's repository
structure. In **Edit → Schedule → Check policy** you can also enable
**download and check a sample of saved data** (off by default). Each check
targets 1% of the stored data, up to 1 GiB per repository, and uses restic's
own authenticated integrity checks. Sources sharing a repository don't repeat
the same check. Download speed follows the backup's limit.

The sample is an approximate budget: restic selects whole packs, at least one
when data exists. Metadata and retries add traffic, and cloud providers may
charge for these reads. A random sample can miss damaged data and doesn't
guarantee that every pack is eventually covered.

The checks answer different questions. Repository checks find inconsistent
metadata; reading data also detects corruption or tampering in the selected
packs. **Test restore** also exercises Backspace's own file selection,
recovery and output handling. None of them proves that every file you meant
to protect was selected for backup.

## Alerts, email and Healthchecks.io

- **Alerts**: a notification and a sound for failures, which can bypass Focus
  Assist. Choose per event (success, skipped, failure) in Settings.
- **Email**: optional, through [Resend](https://resend.com) or Mailgun, using
  the provider's HTTP API and an API key.
- **Healthchecks.io**: paste your API key and Backspace creates and names the
  checks for you.

## Removing a backup

Removing a backup deletes every copy it stored at its destinations. Other
backups' data is untouched, and its run history is deleted once its copies are
gone. If a destination can't be reached, such as an unplugged drive, Backspace
remembers it in `Backspace.config/pending-removals.json` and finishes the
deletion the next time it can reach that destination while Backspace is open.

"Erase destination" and "prune to latest revision" sit behind a type-to-confirm
dialog.

## Metered networks

On Windows, if your laptop switches to a metered connection mid-backup, the run
stops cleanly instead of eating your data plan. The same guard covers
standalone prune and check runs; the interruption and any completed work are
recorded in **Activity & logs**. The option is hidden on macOS and Linux, which
don't expose this signal.

## App updates

**Settings → App updates** checks GitHub for stable releases: monthly by
default, or daily, weekly or never, or manually. Read the release notes, skip a
version, or download and restart when you're ready. Installation waits for
active work, verifies the complete Windows bundle and keeps the portable
configuration folder. A recovery launcher and the previous application files
remain available if the new version won't start. Development builds and
folders without write access offer a manual download instead.

## Portability

Everything Backspace creates lives in **one folder** next to the executable:
`Backspace.config/`. It holds `config.json`, Backrest's state, run logs and the
encrypted `secrets.bin`. The bundled engines (restic, Backrest, rclone) sit
beside the executable.

Copy the executable and `Backspace.config/` to a new computer and your backup
definitions (names, sources, destinations, filters, schedules) come with them.
Re-enter storage passwords and sign in to cloud accounts again: the secrets
vault is bound to the computer (DPAPI on Windows, an AES-GCM key file on macOS
and Linux), so it doesn't travel.

Settings outside that folder are ones Backspace can recreate: the sign-in start
entry, the optional background service, and the WinFsp driver for mounting.

## Importing from Duplicacy

**Settings → Import existing backups** reads either layout:

- the folder containing your `.duplicacy/` directory (or the `.duplicacy`
  file that `-pref-dir` setups leave), or
- a parent folder containing several such repositories (the duplicacy-util
  convention). Restore profiles that reuse a backup's snapshot ID are skipped.

Backspace imports storage URLs, repository paths, filters, and per-storage
threads, keep, prune and check settings. Your Duplicacy storage is **left
untouched**: Backspace starts a fresh restic repository beside it (a
`Duplicacy` folder in the path becomes `Backspace`; Dropbox uses Backspace's
own app folder). Cloud tokens aren't imported, so Backspace asks you to sign in
the first time it uses each destination. Older versions stay in Duplicacy.

Filters keep Duplicacy's meaning: `e:` and `i:` lines, and the first matching
line wins. Rules that are exactly one of Backspace's recommended exclusions
move into the recommended block. Restic's patterns cannot tell files from
folders, so two cases differ: a folder named like a file-only rule (such as
`\.tmp$`) is skipped with its contents, and files lying directly in a folder
kept only to reach a subfolder are kept. Email and Healthchecks settings are
not imported; set them up in Settings.

When Backspace's recommended exclusions change, your saved backups keep their
rules. Editing a backup shows the additions and removals for the groups it
uses, with **Update recommended exclusions** and **Don't suggest these again**.

## Command line

```
Backspace                          # open the app
Backspace --background             # start in the tray only (used at sign-in)
Backspace --run <backup-name>      # run one backup now, without the app
Backspace --run-all                # run every enabled backup
Backspace --prune <backup-name>    # apply the backup's retention policy
Backspace --check <backup-name>    # check the backup's repositories
Backspace --migrate <path>         # import an existing Duplicacy setup
Backspace --version
Backspace --help
```
