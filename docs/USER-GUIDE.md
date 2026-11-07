# Backspace user guide

The [README](../README.md) gives the overview. This guide covers how each part
of Backspace behaves, including its limits.

- [Destinations and cloud sign-in](#destinations-and-cloud-sign-in)
- [Schedules, the tray icon and Quit](#schedules-the-tray-icon-and-quit)
- [Administrator access and files in use](#administrator-access-and-files-in-use)
- [Filters](#filters)
- [Sizes and logs](#sizes-and-logs)
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

External drives are matched by volume name (the drive's name in Explorer),
whatever letter Windows gives them. Picking a folder on the drive fills in its
name. Add a drive letter as well only to require both. A drive without a name
is matched by its letter until you name it in File Explorer (right-click the
drive, then **Rename**) and type the same name in Backspace. A backup whose
drive isn't plugged in is skipped and says so; it doesn't fail.

Backspace never starts a backup over on the wrong drive or network share. The
first backup to one runs when you start it (**Run now** or **Save & Run**).
After that, a run goes ahead only if the drive or share holds the backup. If
it doesn't (another drive with the same name, a replaced or erased NAS, or
the same drive erased), **Run now** asks: **Start a new backup here**, **Don't
use this drive** (or share), or **Not now**, and scheduled runs fail with
"Waiting for you" (so failure alerts reach you) until you answer. **Don't use this drive** leaves a
small hidden file named `.backspace.<backup ID>.declined` at the drive's or
share's root (beside the backup's folder when the share's root is read-only),
so it is skipped for that backup only, on any computer, without asking again;
other backups still use it. Choose **Run now** again, or delete the file, to
use it after all.

For cloud storage, choose **Connect** and approve your own account. Backspace
ships its application registrations, so you don't need to create one. Dropbox
and OneDrive use Backspace's own app folder. Google uses a private app-data
folder that is hidden from the Drive file list; browse and restore those
backups in Backspace. Your account tokens stay in your local encrypted vault.
See the [registration guide](OAUTH_REGISTRATION.md) for provider setup and
testing requirements.

### How long backups take

One measurement on a home connection (2 November 2026), backing up 10,000
files totalling 1 GiB, then again after rewriting a quarter of them, then once
more with nothing changed:

| Destination | First backup | 25% rewritten | Nothing changed |
|---|---|---|---|
| Local SSD | 14 s | 8 s | 7 s |
| Google Drive | 7 min 2 s | 2 min 32 s | 39 s |
| OneDrive | 9 min 45 s | 3 min 36 s | 1 min 28 s |
| Dropbox | 10 min 49 s | 4 min 51 s | 41 s |

The first backup to a cloud is mostly upload time, so your connection's upload
speed sets the pace; later backups send only what changed. A backup with
nothing new still has to list the cloud copy, which is why it takes tens of
seconds rather than the local disk's few. Treat these figures as a guide,
give or take 20%.

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
  while it is not. Backups to a network share run in the service when the
  share's user name and password are saved with the destination: the service
  runs as Windows itself, so it cannot use your mapped drive letters or the
  password Windows remembers for you. Without them, those backups stay with
  the tray app, which runs them while you are signed in.

The tray icon's check mark means the backups ran successfully; a backup that
failed shows an exclamation mark, and one that hasn't completed a run yet a
grey dash. A backup that has never run has nothing to restore yet, so its
Mount, Restore files, Test restore and Browse snapshots are greyed out.

Closing the window keeps Backspace in the tray, so schedules keep running.
**Quit** from the tray menu stops Backspace completely, including the
background service: active work stops and nothing runs until you open
Backspace again, sign in again, or start Windows again (a restart, or
switching the computer on after **Shut down**, Fast Startup included). Sleep,
hibernation and locking the screen don't bring it back.

Backspace registers no Windows scheduled task. Between runs it only waits for
the next due time, so it uses no CPU while idle. The backup engine (Backrest, and the
restic and rclone it starts) runs only while needed: the background service
stops it when each run ends, and the open app after 5 minutes without use. The
next action starts it again, which takes a few seconds.

After each backup (or on its own schedule, if the backup has one), the
cleanup removes the snapshots its keep policy no longer
keeps and deletes the stored data only they used. It also runs a full prune
after a backup that follows one cut short, and at least once a week, which
deletes data no snapshot uses, such as the first copy of what an interrupted
backup sent again. While it runs, the storage can't be used by another
operation.

The log ends the cleanup with what it removed ("[cleanup] 2 old backups removed
· 1.3 GB freed in the storage"), and the backup’s **History** adds "1.3 GB
freed" to its run when something was freed. With what each backup added, it
shows why the storage doesn’t only grow.

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

Backspace doesn't keep the computer awake or hold back restarts, including
those Windows Update schedules: when to sleep or restart stays your choice
and Windows'. When the computer sleeps, a backup pauses and carries on when
it wakes. A backup cut short instead (by a restart, Windows shutting down, a
power loss, a crash, an update of Backspace or Quit) starts again by itself
when Backspace runs again, even with "Catch up missed runs" off. What it had
already stored is reused, except what it sent in its last few minutes: the
engine records what it stored every 10 minutes, so that part is sent again, and
the cleanup after the next backup deletes the first copy. Its history says "Interrupted", and you get no
notification, since there is nothing to do. This applies to scheduled
backups, and with administrator access to `Backspace --run` from the
command line as well; a backup started with **Run now** in the window
waits for its next scheduled time. A resume stopped by Windows or an update
resumes again; one ended by a crash or a power loss doesn't, so a backup
that brings the computer down doesn't start at every launch: the next
scheduled time is the next try, and you are notified. **Stop** ends a backup
without resuming it.

On macOS and Linux, Backspace starts as a login item and keeps the schedule
the same way while you are logged in (untested).

## Administrator access and files in use

New backups use Windows snapshots (VSS) by default. VSS includes files other
programs have open and captures everything at one point in time.

VSS needs Backspace's administrator access. Creating a backup includes
**Allow administrator access**, ticked by default; Windows asks you to approve
it when you save, before the first run (a standard account enters an
administrator's password). That installs Backspace's background service, one
per Windows account. It runs as Windows itself, from the portable
`Backspace.exe` wherever you keep it, so there is no second copy, and Backspace
changes no file or folder permission anywhere. Its working files stay in
`Backspace.config\service`. Like other backup programs it uses Windows' backup
rights, so it reads every file you choose whatever its permissions. Network
drives can't hold Backspace for this. You can turn access on in Settings before
creating any backup; backups added later run in the service too.

Because the service runs `Backspace.exe` as Windows itself, anyone who can
replace that file can run programs with full control of this computer. Keep
Backspace in a folder only you (or administrators) can change, as your user
profile and Dropbox folder are. For the same reason, an update is trusted on
GitHub's own checksum and a secure connection: `Backspace.exe` is not
code-signed, so someone who took over the project's GitHub releases could
publish an update the service would then run.

With access on, **Run now** goes through the same service and includes files
in use without another prompt. Adding, editing or removing a backup asks
nothing either: the service reads your saved settings within a minute. Updating
Backspace asks nothing too: the service stops while `Backspace.exe` is
replaced and starts again. **Settings → Administrator access** keeps **Remove
access**, which asks Windows once and removes it. The service's history stays
in Backspace.

The service reads the same saved passwords as the app (see **Saved passwords**
below). When you set it up, and whenever you open Backspace afterwards, the app
gives it this computer's key to them; nothing is copied. If it has no key yet
(for example, right after you started over with new saved passwords), its
backups fail with "Open Backspace on this computer and enter your password once."
Opening Backspace does that.

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

A backup's filters are a restic exclude file, and Backspace hands that text to
restic as it is: `restic backup --iexclude-file` on Windows (upper and lower
case don't matter) and `--exclude-file` on macOS and Linux. Nothing is
rewritten on the way, so the same file gives the same backup with restic
alone. **Filters → Advanced** shows the text, with a simulation pane that
lists what would be kept or left out before you run.

How restic reads the rules, one per line (`#` starts a comment):

- A rule is matched against the full path. On Windows that path starts with
  its drive (`C:/Users/me/…`), so `?:/` means the top of any drive:
  `?:/Users/*/AppData/Local/Temp/` is each profile's Temp folder and nothing
  deeper down. A rule starting with a plain `/` matches nothing on Windows. On
  macOS and Linux `/` is the top of the file system: write the full path
  (`/home/*/.cache/`).
- A rule without `?:/` or `/` matches at any depth, including the backed-up
  folder itself and the folders above it: `node_modules/` anywhere.
- `*` matches within one name, `**` any number of folders, and a rule also
  covers everything inside what it matches. A trailing `/` changes nothing for
  restic: `Cache/` also leaves out a file named Cache.
- `$NAME` is an environment variable, so a literal `$` is written `$$`
  (`?:/$$RECYCLE.BIN/`). Written `?:/$WinREAgent/`, an unset variable would
  leave `?://`: the whole drive.

A rule starting with `!` is an exception, and the last rule that matches a
path decides. The engine never goes into a folder it excludes, so an
exception below a folder excluded whole keeps nothing: `cache/` then
`!cache/keep/` backs up nothing from `cache`. Exclude the folder's contents
instead (`cache/*` then `!cache/keep/`). **Filters → Advanced** warns about
such an exception and shows the fix, and about a rule restic can't read (such
as an unclosed `[`): restic refuses the whole list, so the backup would stop.

The essential exclusions (below, under Sizes and logs) are written into each
backup's text as their own block at the end, headed
`# === Backspace essential exclusions (always applied) ===`. New backups get
it, and **Apply recommended exclusions** keeps it last.

The engine checks every rule against every file and folder it looks at. With
hundreds of rules that used to take most of a large backup's time; on Windows,
Backspace's build of the engine skips a rule at once when a path can't match it,
which makes this about 5 times faster.

**Filters → Recommended** groups the suggested exclusions (Windows system
files, app caches, cloud-synced folders). A green **✓ Applied** line means
the ticked groups are already in the backup's filters and there is nothing
left to do; **Apply recommended exclusions** is enabled again only when you
change a tick. **Estimate savings** reads the backup's folders and says how
much the ticked recommendations leave out of them. On **Filters → Advanced**,
**Count** says how much all the rules leave out, your own included, and
**Simulate** lists what each rule keeps or leaves out.

Recommended rules start at the top of the drive (`?:/Windows/`,
`?:/Users/*/AppData/**/Cache/`), so a folder you made deeper down, such as an
old PC's copied profile, is never matched. Rules useful wherever they are
(`node_modules/`, `__pycache__/`, `*.tmp`, `*.temp`, `.DS_Store`, thumbnail
caches) match at any depth. Only cloud-synced folders inside the backup's
folders are offered, and only rules that can match there: a backup of
C:\Users\me gets the profile rules, a backup of a Dropbox folder is not offered
the Windows folder or the profiles' AppData rules, and no rule is offered that
would leave out a whole backed-up folder.

Recommendations only name things that can be rebuilt or downloaded again:
app and browser caches, Windows' own temp folder for each account, `.tmp` and
`.temp` files, package caches (npm, NuGet, Gradle, pip, Go), thumbnail caches,
`node_modules`, and, in the Windows group, the Windows folder, Windows.old, the
Recycle Bin and installer folders. Folders named temp or tmp elsewhere,
`.dump` and `.dmp` files, apps' Logs folders, Store apps' LocalCache and the
registry files are not recommended: they can hold someone's own work or
settings. On macOS and Linux the profile rules start at `/home/*/` or
`/Users/*/`, and the Windows ones are not offered.

## Sizes and logs

Before a backup's first run, each source on its card shows the size of what
that backup will hold ("~812 GB", "~" for an estimate): its exclusion rules are applied, and
excluded folders aren't even read. Files kept only in the cloud (Dropbox,
OneDrive or Google Drive placeholders) aren't counted: they aren't on the
computer, and backups skip them. Hover the size for how it was measured.
Backspace reads several folders at once, without opening each file. A
source too big to read within three minutes shows what was read with "+"
("812+ GB");
a whole drive with no exclusion rules shows the space used on the drive
instead. After a backup runs, the card shows the size that backup read. With
administrator access on, the background service does the counting, with the
rights of its backups, and its backups reuse that count to show their
percentage from the start: a folder isn't counted twice. A count is reused for
at most 10 minutes; after that the folder is counted again, which takes seconds.

Some folders can't be opened from your account, such as another account's
profile. With administrator access on, backups include them and the size
counts them. Without it, backups leave them out, and so does the size; the
settings window then says how many folders won't be backed up, naming one.

A few Windows files are left out by the essential block each backup's filters
end with (see Filters), and never counted, because no restore can use them:
the page, swap and
hibernation files (pagefile.sys, swapfile.sys, hiberfil.sys, DumpStack.log.tmp),
System Volume Information (shadow copies and the search index), the temporary
folders of Windows Update, upgrades, resets and installers (Config.Msi,
$WinREAgent, $GetCurrent, $SysReset, $Windows.~BT, $Windows.~WS,
Windows10Upgrade, OneDriveTemp) and the boot files at the top of the drive
(bootmgr, BOOTNXT, BOOTSECT.BAK), and in every profile Windows' app shortcuts
(AppData\Local\Microsoft\WindowsApps), links the engine can't store. Windows, Windows.old, the Recycle Bin and the registry hives are only
recommended exclusions: a backup can include them. The Recovery folder is
never excluded: a manufacturer may keep recovery files there.

While a backup runs, its card says **Running** with a turning icon; each
source and the strip below show the percentage, and hovering **Running** shows
bytes, files and speed, for example "30 / ~178 GB · 88,211 / ~109,320 files ·
2.6 GB sent at 5.7 MB/s". The totals, marked "~", are Backspace's own listing
of the source with the backup's rules, made as the backup starts (seconds for
a whole drive). The engine doesn't count the source itself: its separate
counting pass looked every file up a second time and took as long as the backup
walk. Until the listing is done, the size is the last backup's, the percentage
is of that size, and no time left is shown.
The speed is of the data actually sent to the storage, over the last half
minute, and shows whenever new data is being sent, however slowly. Files that
need nothing sent (unchanged, or already stored) are checked quickly and don't
count, nor does the engine's own bookkeeping (its folder lists and index), so
while the engine reads files the storage already holds, the amount sent hardly
grows and no speed is shown. That
happens when a backup starts again after an interruption, or has no earlier
backup to compare with: the engine reads every file to find what the storage
already holds. The time left follows the pace of the last five minutes, or of
the last minute when that is slower: the engine can't tell which of the
remaining files are new, so a large changed file lengthens the estimate at
once, and it shortens again when the pace picks up. Once the source is listed,
its size next to the folder is the size counted by this run.

**After a first backup, the progress follows the changes.** Once a backup has
succeeded, Backspace keeps a list of the source's files with their size and
date (in `%LOCALAPPDATA%\Backspace\change-lists`; a cache it rebuilds when
missing). At the start of the next backup it lists the source again (seconds)
and compares: a file whose size or date differs is one the engine will read,
as the engine itself decides. The line then reads, for example, "23.4% ·
Backing up · changes 3.1 / ~9.0 GB · 504,701 / 601,922 files, 190 GB · 881 MB
sent at 3.8 MB/s · about 25 min left". The percentage and the time left are of
the work: checking the unchanged files counts for a small part, reading and
sending the changes for the rest, each at the pace this backup measures (the
last backup's pace is the first guess). So a backup no longer races to 99% on
unchanged files and then waits; the size next to the source stays the
source's. The changed size is what the engine reads: it can send much less,
since it stores only the parts it doesn't hold yet. Without the list (a first
backup, or a list made for another snapshot), progress is shown as above.

**Activity & logs** shows each run as phases. A backup opens with what it
starts from: the previous backup to that destination (date and snapshot), the
number of exclusion rules, and whether a shadow copy is used. Progress follows
every few seconds (percent, bytes and files done, data sent and its speed,
and an estimate of the time left once the files are counted), then a closing recap: files checked, new, changed and
unchanged, data added to the storage, duration and the snapshot made.
Long lines wrap. Tick **Verbose** to also show the engine's debug lines
and status polls. Run logs always record every line, so ticking it shows the
details of past runs too. It is the same setting as **Settings → Logs &
diagnostics → Show verbose diagnostic logs**, which also makes Backspace's own
app log record debug lines from then on. While Verbose is on, each backup
also records one line per file it added, changed or removed, with its full
path, compared with the previous backup (a first backup lists every file).
The list is read from the two snapshots right after the backup, so it adds a
little time, and only Verbose shows it. **Clear view** appears on the live
view only; a saved run's log can't be cleared.

A backup's **History** lists its runs, each backup with what it added
("14,145 new · 239 changed · 12 GB added · 1.3 GB freed", the last part
only when its cleanup freed space); a cleanup on its own schedule lists what
it removed and freed. **Files added** shows those files
without Verbose: a map of the folders they are in, each box sized by the bytes
of its new and changed files, like WinDirStat, and the largest files. Click a
folder to open it; **Up** goes back. It compares the backup's snapshot with
the one before, by size and date, reading only their file lists (cached on
this computer), so it works as long as both snapshots are kept. Sizes are of
the files; the storage received less where parts were already stored.
**Open log** shows the run's log. Runs the background service made are read
from its history and logs in the portable folder, in full, even while the
service is stopped; the app never asks the service for them.

A backup that gets stuck fails instead of running forever. If the engine
leaves every status check unanswered for 10 minutes, or a backup's counts
(files and bytes found and done) don't move for 60 minutes, Backspace stops it
and the run fails with that reason and the usual alerts; the next scheduled
run tries again. A busy engine that answers slowly, or a backup that keeps
moving however slowly, is left alone.

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

Everything Backspace keeps lives in **one folder** next to the executable:
`Backspace.config/`. It holds `config.json`, run history and logs, and your
saved passwords (`vault.bin`).

Copy the executable and `Backspace.config/` to a new computer (or let Dropbox
do it) and everything comes with them: backup definitions, history and saved
passwords. On the new computer Backspace asks for your saved-passwords password
once, the first time something needs it; until then the Backups page shows a
banner, and scheduled backups wait.

### Saved passwords

Storage passwords, cloud sign-ins and email or Healthchecks.io keys are kept
in `vault.bin`, locked with a password you choose. Backspace asks you to
choose it the first time it saves one of them, not before, and remembers it on
each computer after you enter it there once. You can leave it empty: then the
saved passwords are only **obscured, not protected**. Anyone who gets a copy of
the Backspace folder (for example from your Dropbox) can read them, and with
them your backups. Choose or change the password in **Settings → Saved
passwords**; other computers that already use the folder keep working.

If you forget the password, the saved passwords can't be recovered. In
**Settings → Saved passwords**, choose **Forgot the password? Start over**:
Backspace keeps the old file beside the new one (`vault.bin.forgotten-…`,
which the old password still opens), then you enter your storage passwords and
sign in to cloud destinations again.

Files that would show a password in readable form are kept on each computer,
not in the folder: the engines' working files (Backrest's settings and
database, cloud connection files) in `%LOCALAPPDATA%\Backspace\engine`, rebuilt
when missing, and each computer's key in `%LOCALAPPDATA%\Backspace\keys`. On a
new computer the engine's own list of past operations starts empty; your run
history, which lives in `Backspace.config`, is unaffected.

An install from before saved passwords travelled kept them in a file only that
Windows account on that computer could open (`secrets.bin`). At the first start
after updating, Backspace asks once to choose a password and moves them,
including the ones the background service held; it checks every one before
removing the old files, and removes the old file only after the next backup
succeeds.

Settings outside that folder are ones Backspace can recreate: the sign-in start
entry, the optional background service, the WinFsp driver for mounting, and
`%LOCALAPPDATA%\Backspace`, which holds the engines (downloaded from the same
GitHub release and checked against fingerprints built into `Backspace.exe`)
and caches, the engines' working files and this computer's key to your saved
passwords.

Backspace keeps one copy per Windows account. Opening another copy, such as a
new download, while Backspace is set up elsewhere offers **Update that copy**
(this version replaces the other `Backspace.exe`), **Move Backspace here**
(settings, history and saved sign-ins move to this folder and the other
`Backspace.exe` is deleted; the background service stops and starts again from
here, which Windows approves once) or **Cancel**.

**Settings → Remove Backspace from this PC** removes everything Backspace
added to Windows: the background service,
the sign-in entry, the Explorer menu entry, `%LOCALAPPDATA%\Backspace` and tasks
left by older versions. WinFsp is uninstalled only if you tick it, since other
apps may use it. Backspace then quits; `Backspace.exe`, your settings and your
backups stay, so deleting that folder removes the rest. This computer's key to
your saved passwords goes too: if you open Backspace here again, it asks for
their password once.

## Importing from Duplicacy

**Settings → Import existing backups** reads either layout:

- the folder containing your `.duplicacy/` directory (or the `.duplicacy`
  file that `-pref-dir` setups leave), or
- a parent folder containing several such repositories (the duplicacy-util
  convention). Restore profiles that reuse a backup's snapshot ID are skipped.

Backspace imports storage URLs, repository paths, filters, and per-storage
threads, keep, prune and check settings. A storage on another drive becomes
an external drive matched by its volume name, read from the drive or, while it
is unplugged, from a run script's check such as
`wmic logicaldisk where "VolumeName='My Passport' and Name='M:'"`. Your
Duplicacy storage is **left
untouched**: Backspace starts a fresh restic repository beside it (a
`Duplicacy` folder in the path becomes `Backspace`; Dropbox uses Backspace's
own app folder). Cloud sign-ins and storage passwords aren't imported: until a
destination has them, **Run now** opens it with a note saying what to finish
(connect the account, choose a storage password), and a scheduled run stops
with the same reason instead of retrying. Older versions stay in Duplicacy.

Filters are converted once, at import, into plain restic rules with
Duplicacy's meaning; backups imported by an earlier version are converted the
first time Backspace starts. Duplicacy lets the first matching `e:`/`i:` (or
`-`/`+`) line decide and restic the last, so each converted rule sits in
restic's order under a `# was:` comment naming the line it came from. A rule
tied to the top of the backed-up folder (`^Apps/…`) becomes a rule from the
top of the drive with the folder's path (`?:/Users/me/Dropbox/Apps/…`); the
others match at any depth. The Duplicacy explanation comments are dropped, and
a line restic can't express is named in a comment rather than guessed. Rules
that are exactly one of Backspace's recommended exclusions move into the
recommended block. Restic's patterns cannot tell files from folders, so two
cases differ: a folder named like a file-only rule (such as `\.tmp$`) is
skipped with its contents, and files lying directly in a folder kept only to
reach a subfolder are kept. One list ignores case or doesn't, so on Windows a
line without `(?i)` ignores case too. Email and Healthchecks settings are not
imported; set them up in Settings.

When Backspace's recommended exclusions change, your saved backups keep their
rules. Editing a backup lists the additions and removals for the groups it
uses, each with a checkbox: rules added to the lists, rules missing from the
backup's block (one you deleted is offered again), and rules that can't match
in the backup's folders (offered again if you add a whole drive). **Apply ticked changes** applies the ticked ones;
the unticked ones are not suggested again for that backup, and
**Don't suggest these again** declines them all. Declined suggestions are
counted below the list; **Show hidden suggestions** lists them again, unticked. A declined suggestion
that Backspace no longer recommends is dropped, so it never comes back.

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
