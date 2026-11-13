# Backspace user guide

The [README](../README.md) gives the overview. This guide covers how each part
of Backspace behaves, including its limits.

- [Saving changes](#saving-changes)
- [Destinations and cloud sign-in](#destinations-and-cloud-sign-in)
- [Schedules, the tray icon and Quit](#schedules-the-tray-icon-and-quit)
- [Administrator access and files in use](#administrator-access-and-files-in-use)
- [Filters](#filters)
- [Files kept only in the cloud](#files-kept-only-in-the-cloud)
- [Sizes and logs](#sizes-and-logs)
- [Restoring files](#restoring-files)
- [Browsing and mounting snapshots](#browsing-and-mounting-snapshots)
- [Checks and Verify](#checks-and-verify)
- [Alerts, email and Healthchecks.io](#alerts-email-and-healthchecksio)
- [Removing a backup](#removing-a-backup)
- [Metered networks](#metered-networks)
- [App updates](#app-updates)
- [Portability](#portability)
- [Importing from Duplicacy](#importing-from-duplicacy)
- [Command line](#command-line)

## Saving changes

**Settings** has no Save button: each change is saved as you make it, and a
small **Saved** shows at the bottom right of the page, wherever it is
scrolled. If a change can't be saved, a red notice along the bottom says why
and stays until a change is saved.

The backup and destination editors save when you click **Save**. Closing one
with changes not saved (its X, **Cancel** or Escape) asks **Save your
changes?**: **Save** saves and closes, **Don't save** closes without them, and
**Keep editing** returns to the editor. Closing an editor you didn't change
doesn't ask.

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
  while it is not. Backups to a network share run in the service too: it
  runs as Windows itself, so it cannot use your mapped drive letters or the
  password Windows remembers for you, and signs in to the share with the user
  name and password saved with the destination. With access on, the
  destination editor requires them, **Run now** opens the destination to enter
  them when they are missing, and Settings → Administrator access names any
  share still without them; a run without them fails saying what to enter.

The tray icon's check mark means the backups ran successfully; a backup that
failed shows an exclamation mark, and one that hasn't completed a run yet a
grey dash. A backup that has never run has nothing to restore yet, so its
Mount, Restore files and Verify are greyed out.

Closing the window keeps Backspace in the tray, so schedules keep running.
**Quit** from the tray menu stops Backspace completely, including the
background service: active work stops and nothing runs until you open
Backspace again, sign in again, or start Windows again (a restart, or
switching the computer on after **Shut down**, Fast Startup included). Sleep,
hibernation and locking the screen don't bring it back.

Backspace registers no Windows scheduled task. Between runs it only waits for
the next due time, so it uses no CPU while idle. The backup engine runs only
while needed. Backups, their cleanup and checks, listing, browsing and
restoring backups run restic (and rclone, for a cloud destination), which end
with them; nothing of the engine stays running in between.

After each backup (or on its own schedule, if the backup has one), the
cleanup removes the snapshots its keep policy no longer
keeps and deletes the stored data only they used. It also runs a full prune
after a backup that follows one cut short, and at least once a week, which
deletes data no snapshot uses, such as the first copy of what an interrupted
backup sent again. While it runs, the storage can't be used by another
operation.

### Retention policy (which old backups are kept)

The **Retention policy** box in the backup editor takes Duplicacy's `n:m`
rules: for backups older than m days, keep one every n days; `0:m` deletes
backups older than m days; everything younger than the smallest m is kept.
`0:365 30:180 7:90 1:14` keeps every backup of the last 14 days, the last
backup of each day up to 90 days, of each week up to 180 days, of each month
up to 365 days, and nothing older. A single number N keeps the latest N
backups; an empty box keeps everything. The editor explains the rule as it
runs, under the box.

Backspace applies it with restic's keep-within options
(`--keep-within=14d --keep-within-daily=90d --keep-within-weekly=180d
--keep-within-monthly=365d` for the example). Two differences from Duplicacy:

- Ages count back from the **latest backup**, not from today. If backups
  stop, for a holiday or an unplugged drive, nothing more is deleted.
- Restic keeps the last backup of each calendar day, week, month or year;
  Duplicacy keeps one every n days, starting from the oldest. Over a year
  they keep about the same number (a month band can hold one more or one
  fewer, as months have 28 to 31 days). An n other than 1, 7, 30 or 365
  uses the next shorter period (one every 10 days is kept as one per week),
  so it never keeps fewer than the rule asks; the editor says so.

A policy the editor refuses (two rules with the same age, a `0:m` rule that
is not the oldest, `0:0`) deletes nothing: the cleanup keeps every backup and
says why in the log.

The log ends the cleanup with what it removed ("[cleanup] 2 old backups removed
· 1.3 GB freed in the storage"), and the backup’s **History** adds "1.3 GB
freed" to its run when something was freed. With what each backup added, it
shows why the storage doesn’t only grow.

**Edit → Schedule** opens with a summary of the backup's timing, one line each for its backups, its cleanup and each check (click one to open its tab), above three tabs: **Backups** (how often, the time and the conditions), **Cleanup** and **Checks**.

A backup has two conditions, in **Edit → Schedule → Backups → Conditions**. Each is a
wait and a stop: a run waits until it is met, stops if it no longer is, and
then runs again as soon as it is.

- **Run only while on AC power**: a run due on battery waits until the
  computer is plugged in; a run that is going stops if it switches to battery.
- **Run only while on an unmetered connection**: the same for a metered
  connection (a phone's hotspot, a capped mobile plan). It concerns only the
  backup's cloud and network destinations (cloud, SFTP, S3, a network share):
  a destination on this computer (a folder or an external drive) never waits
  for the network. A network share reached through a VPN such as Tailscale
  over a metered connection counts as metered, the safe answer.

They apply to the backup, its cleanup and its checks. A check can have its
own instead: untick **Same conditions as the backup** in its entry.

A run you start yourself (**Run now**, **Verify**) while a condition isn't
met asks first, listing each one on its own: on battery, **Run on battery
anyway**; on a metered connection, **Use this metered connection anyway**.
What you tick is ignored until that run ends; nothing else is. What you leave
unticked holds the run back: **Run when ready** queues it, the card says
Queued, its hover "waiting for AC power since 14:02", and it starts by itself
once the condition is met. The queue is kept in Backspace.config, so it
survives closing the window and restarting Backspace or Windows; **Stop** on
the card cancels it. A backup with a drive and a cloud destination is backed
up to the drive now either way: only its cloud part waits for an unmetered
connection.

A run you started pauses if a condition you didn't ignore stops holding while
it runs: unplugged, it stops; on a metered connection, its cloud part waits.
Backspace says so in a notification: click it to continue anyway until the
run ends, or let it wait, and it runs again by itself once the condition is
met. A backup only sends what changed.

There is no setting for the internet. A backup with a destination on this
computer and one in the cloud backs up the local one on schedule, even when
the cloud can't be reached: offline for a week, the drive still gets each
day's backup. Its cloud part waits, and runs on its own as soon as the
network allows: when the connection comes back or stops being metered
(Windows tells Backspace at once). A network destination that can't be
reached although the network seems fine is tried again when the network
changes, or a day later, never in a loop. A backup whose destinations are all
in the cloud simply waits for the network. Checks work the same way: a
check of a backup with a local destination checks it, and its network
destinations wait for the next backup there.

A run missed while the computer was off, asleep or signed out happens once,
as soon as Backspace is running again. A backup never overlaps itself: if it
is still running when its next run is due, the next is skipped.

### Simultaneous backups

Backups that are due together, or that you start one after the other, run
together up to two limits, in **Settings › Simultaneous backups**:

- **Per destination** (1 by default): backups to the same
  destination share its connection, so with 1 they take turns and the first
  one isn't slowed down by the second.
- **In total** (2 by default): all destinations together, so
  the computer's disk isn't overloaded.

A backup that has to wait says **Queued** on its card instead of **Running**.
Hovering it shows why, for example "Queued: waiting for local_c (same
destination)" or "Queued: 2 backups already running". It starts as soon as its turn comes; **Stop** ends the wait. The time
running shown once it starts leaves out the time it waited, and so does the
finished run's duration ("in 10m 02s"); a wait of a minute or more is listed
apart ("waited 51m 46s"), and the card's hover says "waited 51m 46s for its
turn before that". A backup can also
wait briefly for "another backup's Windows snapshot": Windows makes one
snapshot at a time, and each backup that uses Windows snapshots waits for its
own rather than back up without one.

Backups run from the window to different destinations run together, up to
the limits. Two backups to the same destination take turns in the window even
when the per-destination limit allows more ("Queued: waiting for the backup
engine to finish another task on this destination"). With administrator
access, the background service runs each backup on its own, so those run
together too. Backups with several destinations take their turn for each
destination in order.

The limits apply to backups run from the window, scheduled backups and, with
administrator access, backups the background service runs, together. Cleanup
and checks of a destination always run alone on it, whatever the limits: a
backup reaching its cleanup waits for the other backups on that destination
to finish ("Queued: waiting for local_d (same destination) before cleanup and
checks"), and no new backup starts there meanwhile. Two checks of one
destination never run at the same time either: each waits for the other.
Restore checks are checks too, scheduled or started with **Verify**: they also
run alone on their destination, so two restore checks of one destination take
turns. Only restoring your files is never held back: it only reads, so it can
run while a backup does. A check or a scheduled cleanup that finds the
repository in use by such a restore (or by another computer) waits for it, trying again each minute for up to three
hours, and says "Waiting: another operation is using the repository"; it no
longer fails. With more than one backup
per destination, a new backup's first moments (while the engine prepares the
storage) are still taken one backup at a time.

Backspace doesn't keep the computer awake or hold back restarts, including
those Windows Update schedules: when to sleep or restart stays your choice
and Windows'. When the computer sleeps, a backup pauses and carries on when
it wakes. A backup cut short instead (by a restart, Windows shutting down, a
power loss, a crash, an update of Backspace or Quit) starts again by itself
when Backspace runs again, even with "Run missed backups as soon as possible" off. What it had
already stored is reused, except what it sent in its last few minutes: the
engine records what it stored every 10 minutes, so that part is sent again, and
the cleanup after the next backup deletes the first copy. Its history says "Interrupted", and you get no
notification, since there is nothing to do. This applies to scheduled
backups, and with administrator access to `Backspace --run` from the
command line as well; a backup started with **Run now** in the window
waits for its next scheduled time, and one with automatic backups off waits
for you to run it again. A resume stopped by Windows or an update
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

With access on, **Run now** (and saving with "run after save") goes through the
same service and includes files in use without another prompt. This includes
backups with automatic backups off: they get the same snapshot, but still run
only when you ask. With access on, no backup runs in the window: one the
service can't run fails with the reason instead of running without a
snapshot (a network share without its user name and password, for example). Adding, editing or removing a backup asks
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
alone. **Filters → Advanced → Rules** shows the text across the whole window;
**Left out**, the tab beside it, lists what the rules leave out before you run
(see [What these rules leave out](#what-these-rules-leave-out)).

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

The engine checks every rule against every file and folder it looks at. With
hundreds of rules that used to take most of a large backup's time; on Windows,
Backspace's build of the engine skips a rule at once when a path can't match it,
which makes this about 5 times faster.

**Filters → Recommended** groups the suggested exclusions in three groups you
tick or untick: **Essentials** (first, ticked for every new backup), **Windows
system files** and **App caches and temporary files**, then the cloud-synced
folders. There is no separate Apply: ticking or unticking a group changes the
backup's rules at once, and the window's **Save** keeps the change, as on
every other tab (**Cancel** leaves the backup as it was). Beside each group a
label says where it stands:

- **In place** (green check): the saved backup already has this group;
  nothing to do.
- **Will be added when you save**, **Will be removed when you save** or
  **Will change when you save** (amber): the tick, or a suggestion you used,
  differs from what is saved.
- **Off: these files are backed up**: unticked, and saved that way.

A line at the top of the page says **Saved** (green) when the page is what the
backup uses, or **Not saved yet** (amber) when something waits for Save; the
same "Filter changes not saved yet" note stands beside the Save button
whichever tab is open. Under each group, **Its N rules** opens the exact lines
it puts in this backup's filters (for an unticked group, **Its N rules, if
ticked**). A group with no rule that fits the backup's folders says
**Nothing to leave out in this backup's folders** instead. A tick rewrites only
its group's part of the recommended block, below your own rules, with the
group's rules from the top of the drive first, then those that match at any
depth, each sorted by path; the other groups, and any rule you wrote into the
block, stay as they are. **Estimate savings** reads the backup's folders and
says how much the ticked recommendations leave out of them; **Filters →
Advanced → Left out** shows what all the rules leave out, your own included.

### What these rules leave out

**Filters → Advanced → Left out** shows nothing until you click **Run**. It
then reads the backup's folders with the rules as they are on the Rules tab,
the way the backup reads them (the same matching as restic: full paths, the
last matching rule decides, an excluded folder is never looked inside), with a
progress line; **Stop** ends it. The result:

- One row per topmost item left out, largest first: a folder once, with how
  many files it holds and their size, never its contents; a file alone only
  when its folder is kept. Each row names the rule responsible (the last one
  that matched). Click the rule to see it selected on the **Rules** tab.
- Items of one folder left out by the same rule share a row: a rule such as
  `?:/Users/*/Dropbox/*` leaves out what Dropbox holds but keeps the folder
  itself, so the row reads "Dropbox\…: 14 folders and 3 files inside, the
  folder itself kept". **Show** lists them one by one, each with Keep this.
  Folders like that gather in turn: with `?:/Program Files*/*/*`, Program
  Files is one row ("What 80 folders inside hold, those folders kept") and
  Program Files (x86) another. Rows share a row only where the rule takes any
  name at that level (a lone `*`); `*.tmp` or `Program Files*` name what they
  take, so their matches stay one row each.
- A total ("5 items (16 files), 5.3 MB left out") and a search box for paths
  and rules. There is no cap: folders are collapsed, so even a whole drive
  gives a short list.
- **By rule** groups the same result per rule, with what each leaves out.
- **Keep this** adds an exception (`!` and the item's full path) at the end of
  the rules, so the backup keeps it whatever rule left it out. Like any edit,
  Save keeps it.
- While the rules differ from the saved ones, a run compares the two: rows
  your edits newly leave out are marked **New with your edits**, and **Back in
  the backup with your edits** lists what the saved rules leave out and the
  edited ones keep.
- **Rules that match nothing here** lists your rules that leave nothing out of
  these folders; a rule starting with `/` on Windows says why it never can.
  Recommended rules that match nothing today are counted in one line.
- Folders that couldn't be read are listed apart, and online-only files that
  the backup skips are counted apart. Links are never followed.

When the rules change after a run, an amber line says so; run it again.

In Easy mode the same lists are one box, **Skip junk for me**: ticked, the
backup gets the Essentials, the app caches and, for a whole drive, Windows'
own files, plus the cloud-synced folders inside it; unticked, the
recommended block is removed and only your own rules stay.

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

## Files kept only in the cloud

Dropbox, OneDrive and Google Drive can keep a file "online-only": it appears in
the folder, but its content stays in the cloud until you open it. On Windows,
**Skip cloud-only placeholders** (on by default, in the backup's Windows
options) leaves those files out of the backup without opening them, so nothing
is downloaded. Backspace recognises them by the marks Windows puts on such a
file (offline, or downloaded when opened or read). They are not errors and do
not turn the backup into a warning; the run's log and the **Last backup** hover
say how many were left out ("8,335 online-only files skipped"), and **Left out**
says so too. A file that is skipped this way cannot be restored
from this backup. Make it available offline in the cloud app if you want it
backed up.

With the option off, Backspace reads online-only files as before: the cloud
app may download them and keep them on this computer, and some placeholders
cannot be read at all and are reported as errors.

## Sizes and logs

Before a backup's first run, each source on its card shows the size of what
that backup will hold ("~812 GB", "~" for an estimate): its exclusion rules are applied, and
excluded folders aren't even read. Files kept only in the cloud (Dropbox,
OneDrive or Google Drive placeholders) aren't counted: they aren't on the
computer, and backups skip them (see
[Files kept only in the cloud](#files-kept-only-in-the-cloud)). Hover the size
for how it was measured.
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

The **Essentials** group holds only what no restore can use, and a folder
counted before its backup exists leaves it out too: the page, swap and
hibernation files (pagefile.sys, swapfile.sys, hiberfil.sys, DumpStack.log.tmp),
System Volume Information (shadow copies and restore points), the Recycle Bin,
the temporary folders of Windows Update, upgrades, resets and installers
(Config.Msi, $WinREAgent, $GetCurrent, $SysReset, $Windows.~BT, $Windows.~WS,
Windows10Upgrade, OneDriveTemp), the previous installation an upgrade leaves
(Windows.old), PerfLogs, the boot files at the top of the drive (bootmgr,
BOOTNXT, BOOTSECT.BAK), and in every profile Windows' app shortcuts
(AppData\Local\Microsoft\WindowsApps). Those shortcuts are links the engine
can't store: with the Essentials unticked, each one is an error and a backup
of a profile ends as a warning. The Windows folder and the registry hives are
in other groups or not recommended at all, and the Recovery folder is never
excluded: a manufacturer may keep recovery files there. Before 9 November
2026 these files were a separate block at the end of every backup's rules
that couldn't be unticked; Backspace moved its rules into the Essentials
group of each saved backup when it first started, leaving out the same files.

After a backup, its card's **Last backup** says when and how much was
uploaded, for example **15m ago · 27.4 MB uploaded** (with the number of
warnings, if any; a failed or skipped backup says why). Hovering it shows when
it finished, how long it took, and how many files were new, changed and
deleted; **Activity** lists them too. While a backup or a check runs, the
card's **Activity** starts with it ("Running") and **Open live log**, which
opens Activity & logs on its live output.

While a backup runs, its card says **Running** with a turning icon and the
time the run has taken so far, for example **Running · 12 min** (counted from
the run's start, for a backup run by the background service too, without the
time it spent queued: see [Simultaneous backups](#simultaneous-backups)). Each source
and the strip below show the percentage, and the line under the strip says
what is done, in the order it happens, kept to one line: for example
"88,211 of ~109,320 files checked · about 40 min left". Hovering it shows
everything, one item per line: the size ("30 of ~178 GB"), the new data
uploaded and its speed ("2.6 GB of new data uploaded, 5.7 MB/s") too. The totals, marked "~", are Backspace's own listing
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
as the engine itself decides. The line then reads, for example, "504,701 of 601,922 files checked ·
3.1 of ~9.0 GB of changes backed up · about 25 min left" (or "no changed
files" when nothing changed); the hover adds the size (190 GB) and "881 MB of
new data uploaded, 3.8 MB/s". The percentage and the time left are of
the work: checking the unchanged files counts for a small part, reading and
sending the changes for the rest, each at the pace this backup measures (the
last backup's pace is the first guess). So a backup no longer races to 99% on
unchanged files and then waits; the size next to the source stays the
source's. The new data uploaded is usually much less than the changed files
backed up: only data the backup doesn't already hold is uploaded, compressed,
as hovering the line recalls. Without the list (a first
backup, or a list made for another snapshot), progress is shown as above.

The **Run** picker lists a backup's runs, newest first. Each reads as what it
was (Backup, Data check (1 GiB sample), Restore check, Cleanup), when ("Today
14:38"), a coloured pill with how it ended and how long it took, and one line
of detail; hover it for the whole summary. A run going on now has a blue dot
and "Running". A restore check is listed once, as its check. A run of the
background service, picked while it runs, shows that run's own output, and the
header names it ("local_d · Restore check … (2 of 2)"). **Open live log** in a
backup's Activity picks its running run, even while Activity & logs showed
another backup.

Each check and cleanup keeps a log: where it starts, its steps with their
progress (a line at each step and each further tenth), what it met (a lock
left by an operation that no longer runs is a warning: Backspace removes such
locks, never one in use, and tries again), and how it ended, last. A restore
check's log says what it picks and restores; the list of every file checked
shows with **Verbose**, its result after it. Checks run before version 1.1.40
kept no log: picking one shows what it recorded. restic's own lines are shown
without their UTC time; the line's local time is in the **Timestamps** column.

**Activity & logs** shows each run as phases. A backup opens with what it
starts from: the previous backup to that destination (date and snapshot), the
number of exclusion rules, and whether a shadow copy is used. Progress follows
every few seconds (percent, bytes and files done, data sent and its speed,
and an estimate of the time left once the files are counted), then a closing recap: files checked, new, changed and
unchanged, data added to the storage, duration and the snapshot made.
Lines are coloured by what they are: setup lines dim, progress lines muted
with their percentage bright, warnings and retries amber, errors red, and the recap and
summaries (online-only files skipped, what a cleanup removed) in the accent
colour (also semibold, so it stands out whatever the accent); file paths are
a shade brighter. Where a backup, a check, a cleanup or a restore
starts, a thin rule and a bold header ("local_c · backup · 20:00", where it
ran on the right) keep several runs apart, and the header's **Copy**
copies just that part. Tick **Timestamps** to show the time each line was
written in a narrow column on the left; Backspace remembers the choice (logs
written by earlier versions did not record times, so their column stays
empty). Drag over lines to select them (dragging past the edge scrolls on),
or press Ctrl+A, then Ctrl+C to copy them; Escape clears the selection.
**Copy** in the console's header copies the whole log as shown.
**Open log file** opens a saved run's log in your text editor. A failed run
lists its errors above its log: click one to scroll to its line. Long logs
scroll smoothly, since only the lines on screen are drawn.
Long lines wrap. Tick **Verbose** to also show the engine's debug lines
and status polls, and the run log's own bookkeeping (the fields the header
already sums up, the attempt count, the end time in UTC). Run logs always record every line, so ticking it shows the
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
To stop backing up a folder you opened there (a build output, a cache), click
**Exclude this folder** beside its path. Backspace shows the rule it will add
to the backup's own filter rules, written from the top of the drive
(`?:/Users/you/Projects/app/bin/`), and how much the folder holds now. Confirm
with **Exclude from next backups**: the rule goes at the end of the backup's
custom rules, the settings are saved (the background service uses them too),
and the path shows **Excluded from the next backup** with **Undo**, which takes
that rule out again. Backups already made keep the folder, so it can still be
restored from them. A folder an existing rule already leaves out says so
instead, and when an exception (`!` rule) further down would keep the folder
in, Backspace names it and adds nothing. The backed-up folders themselves
can't be excluded this way; change the backup's folders in its settings.
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

Restores run as you, so you own the files you restore. **Keep the files' permissions**
(under **How should they be restored?**) gives them back who may read and change them, as when they
were backed up; unticked, they get the permissions of the folder they land in,
like files you copy there. It is ticked by default only when restoring to their
original place on the computer that backed them up: elsewhere, saved
permissions may name accounts that don't exist there. A folder your account
can't write to (Program Files, Windows, another account's folders) is said
before anything starts; restore elsewhere instead.

You can leave the wizard and return through **Restores**, pause and resume,
or cancel safely while other work continues. Cancel removes files copied by
that attempt and puts replaced originals back. If another program edited a
restored file meanwhile, Backspace keeps both versions and shows where to find
the original. An unexpected process exit during final file placement can leave
recovery copies beside the destination; automatic recovery from that
interruption is not provided yet.

While a restore runs, its files first arrive in a temporary folder named
`.backspace-restore-…` inside the folder you restore to. Backspace moves them
into place only when the restore finishes. The restore's progress, both in the
wizard and on its **Restores** card, names that folder and offers **Open
folder** so you can watch files arrive. Those files aren't final yet: if you
cancel, the folder is deleted. The line disappears once the restore finishes.
Restore checks don't show it; they check copies in Backspace's own folder.

## Browsing and mounting snapshots

**Browse snapshot** on a backup card walks the snapshot's tree inline, without
starting a restore.

**Mount** opens the snapshot as a read-only drive in File Explorer through the
WinFsp filesystem. Open files in your usual apps, compare them with your
current copies, or drag files out. Mounting needs the WinFsp driver: Backspace
checks for it and asks for administrator approval to install it when it's
missing. Backspace itself stays portable, and normal restores don't need the
driver.

Mount first asks which revision the drive should show. The backup's
revisions are listed newest first with their date, time and, when the
snapshot recorded them, its number of files and size; the latest is ticked.
Tick another one instead to look at an older state, or tick several (or
**Select all**) to compare them; **Enter** mounts what is ticked. With one
revision, its files are at the root of the drive. With several, the drive has
one folder per revision, named by its date and time (`2026-11-05 09-12`). A
backup with several source folders has a folder per source, each following
the same rule.

From then on the card says **Mounting** and what it is doing. Before the drive
appears, Backspace loads the list of files and folders of each chosen revision:
for a large backup in the cloud this can take a while per revision (the card
says which revision and counts the files it has loaded), and **Cancel** stops it.
When the revisions recorded their number of files (the number the picker
shows), the bar fills with a percentage for the whole mount, for example
**Reading the backup's folders, revision 2 of 3: 41%**; a larger revision
counts for more of the bar. The percentage is an estimate: it never goes
backwards and only reaches 100% once everything has been read. For an older
revision that recorded no file count, Backspace estimates from another chosen
revision of the same folder, or the bar keeps moving without a percentage
while that revision is read.
After that, browsing the drive is instant, because a snapshot never changes.
When the drive is ready, Explorer opens it and the card says **Mounted as Z:**
and which revision it shows, with **Open in Explorer**. A file is downloaded
only when it's used: opened, previewed, or shown as a thumbnail. Copying a
file out of the drive works like copying from a slow network share: the copy
starts as soon as the first bytes arrive and its progress bar follows the
download, while the rest of the drive stays responsive. Copying a large folder out of the drive fetches its files
one by one: **Restore files** is faster for that. To see another revision,
**Unmount** and mount again.

While a snapshot is mounted, and while a restore or a restore check runs,
Backspace changes nothing in that backup's storage: cleanup after a backup
waits ("Cleanup waits: a snapshot of this backup is mounted as a drive") and
runs after a later backup, and removing snapshots, erasing the storage or
changing its password say they have to wait. Backups keep running: they only
add a new snapshot, and the drive keeps showing the one it opened.

Backspace removes the drive when you unmount or quit; if Backspace stops any
other way, Windows removes it with it. Closing the window to the tray keeps
the drive, like backups keep running.

## Checks and Verify

A successful backup run is a claim: bytes were uploaded and the engine
reported success. Checks turn that claim into proof. There are three kinds,
each answering one question:

- **Structure check** — *is the backup complete?* It reads the backup's
  records and confirms that every piece of data they point to is still
  there (`restic check`). It reads no file contents, so it is quick.
- **Data check** — *is the stored data intact?* It checks the structure
  first, then downloads stored data, from any backup old or new, and checks
  each piece against its fingerprint. It reads a **sample** (1% of the stored
  data, up to its limit: 1 GiB by default) or, with **All data** ticked, all
  of it: the most thorough check of the storage, and the slowest. It doesn't
  rebuild any file.
- **Restore check** — *can your files come back?* It rebuilds real files
  from the latest backup into a temporary folder, the way a restore does:
  following the folder list, unlocking them with your password and writing
  them to disk. Each time it takes files never tested first, then those
  tested longest ago, so over time it goes through the whole backup.

A backup's checks are a list in **Edit → Schedule → Checks**: one entry per
check, each with its own frequency (after each backup, daily, weekly, monthly
or yearly) and time of day. It follows the backup's conditions, unless you
untick **Same conditions as the backup** to give it its own (**Run only while
on AC power**, **Run only while on an unmetered connection**). **+
Structure check**, **+ Data check** and **+ Restore check** add one;
**Remove** removes it. Cleanup (removing old copies and freeing their space)
stays its own setting above, under **Cleanup**.

New backups get a weekly cleanup, a weekly data check of a 1 GiB sample
(which checks the structure too, so no separate structure check) and a
monthly restore check (1% of files, at most 1 GiB), at 3 AM (Sunday for the
weekly ones, the 1st for the monthly one). Easy mode shows this as one
sentence and no settings.

### Verify

Choose **Verify** on a backup card. Its window lists the backup's own checks,
ticked, each with its latest result ("Passed 2d ago"), then the other kinds
to run once without adding them to the backup. **Run** runs the ticked ones
one after another; they show under Backup health like scheduled checks, and
**Stop** ends them. A restore check run once comes last and runs like the
others: it waits its turn on the destination, shows on the card with
**Stop**, and its result goes to the backup's Activity. Choose its scope:

- **Check a sample** defaults to 1% of the stored file sizes and a 1 GiB
  download budget. It uses the smaller limit, shared across source and
  destination pairs. Whole files are selected, and the budget is never
  exceeded: a file larger than what is left of it is passed over for a
  smaller one. When every file is larger than the budget, nothing is restored
  and the result says so: raise the budget to test that backup. These sizes
  describe recovered files, not exact network traffic: compression, caching
  and repeated destinations affect transfers.
- **Check every file** recovers the latest snapshot of every source at every
  destination, including empty files. Large cloud backups can take hours or
  days. A successful sample never claims this full coverage.

restic validates the data a restore check reads, and Backspace checks the
recovered files and their sizes; your current originals aren't needed.
**Also compare with today's originals** is optional. Matching size and
modification date make an original eligible for this byte-for-byte
comparison; they don't prove it never changed. A difference fails the
comparison but doesn't establish which copy changed. With every file
selected, it also looks for unchanged, included local files that are missing
from the snapshot. A restore check can use substantial bandwidth and
temporary disk space.

### While checks run, and their results

Checks due at the same time run one after another, never together, cheapest
first, and backups wait for them on that destination. Each check's run in
**Activity** says what happened in plain words, such as "Data check (1 GiB
sample) passed in 7 min 50 s". The card's **Backup health** line names the
latest result by the same names ("Data check passed · 4m ago"; a failure
stays shown until that check passes again), and its hover lists each check's
latest result. While a check runs, **Backup health** says so with a turning
icon and the check's name, such as "Data check (1 of 2) · 7 min". Its bar and current
step ("Reading the data sample · 20 of 64 packs", or "Restoring and comparing
files · 120 of 400 files") show below the sources and destinations, where a
backup's progress shows. The bar follows restic's own counts, so it is
approximate in time. Its hover has the check's full name and size, the
destination, the time running, what the check does and the latest result.
**Last backup** keeps showing the last backup's result meanwhile. A backup
that has not stored anything yet says "No checks yet: nothing backed up so
far".

A restore check shows on its backup's card, not in **Restores**, which lists
only the restores of your files. **Stop** removes its temporary copies. If
the app closes unexpectedly, reopening it resumes it; completed batches are
kept, and an unfinished batch may be downloaded again. Changed source or
destination settings prevent resuming, and the backup's Activity says why.

A restore check's temporary copies are made in Backspace's own working
folder (under LocalAppData, never in Backspace.config or a synced folder) and
take that folder's permissions, not the ones saved with each file: a copy of
a system folder that only Windows may open would otherwise be unreadable to
you, and impossible for Backspace to delete. Restoring your files keeps their
saved permissions. If Windows still refuses to delete a temporary copy for
two minutes, Backspace stops waiting and says where it was left; a file
another program has open is waited for as long as it takes. Copies are
removed after each batch of up to 128 files or 256 MiB; a larger file is
handled alone. Before each batch, Backspace checks for space for two copies
plus 64 MiB of reserve. Original files are never replaced.

A data check's sample is an approximate budget: restic selects whole packs,
at least one when data exists. Metadata and retries add traffic, and cloud
providers may charge for these reads. A random sample can miss damaged data
and doesn't guarantee that every pack is eventually covered. Download speed
follows the backup's limit.

None of the checks proves that every file you meant to protect was selected
for backup.

### A check that finds the drive unplugged

When a scheduled check comes due while its destination can't be reached (an
external drive not connected or not the expected one, or a network share
offline), it isn't a failure: that destination's part is skipped and stays
overdue. It runs by itself right after the next backup that reaches the
destination, once, whether the tray app or the background service runs the
backup. Each check waits on its own. Meanwhile the card's check line reads
"Restore check overdue: waits for the drive" (or "Checks overdue: …"
when several wait), and its hover says when the check was missed and that
nothing else is needed. A backup that doesn't reach the drive (still
unplugged, or failed) leaves the check waiting. A check that fails for any
other reason is reported as before and waits for its next scheduled time.

For a drive you connect rarely, a monthly check thus runs the first time the
drive is back and backed up to.

## Alerts, email and Healthchecks.io

- **Alerts**: a notification and a sound for failures, which can bypass Focus
  Assist. Choose per event (success, skipped, failure) in Settings.
- **Email**: optional, through [Resend](https://resend.com) or Mailgun, using
  the provider's HTTP API and an API key.
- **Healthchecks.io**: paste your API key and Backspace creates and names one
  check per backup for you (when you save the key, and for a backup added
  later when Backspace starts). Each backup run pings its check when it
  starts and when it succeeds or fails. A backup with checks gets a second
  check, "<name> · checks", pinged when a check passes or fails. Each
  check expects a ping within the backup's quiet skipped-run alerts period
  (for the checks' check, the longest time between two of its checks, if
  longer), then waits a grace of 3 days, 7 from a month, 14 from six months,
  before Healthchecks.io says it is down. Changing the backup's settings
  updates them. Every integration of your project,
  its email included, is on for these checks, so Healthchecks.io tells you
  when a backup stops pinging. Settings says in green that the key works, or
  in red what is wrong with it. Removing a backup removes its check too; a
  check you linked yourself is kept.

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
configuration folder. A recovery launcher (`Recover Backspace.cmd`) and the
previous application files remain available in `Backspace.config/updates` if
the new version won't start; nothing else is ever written beside the app. Development builds and
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
not in the folder: the engines' working files (cloud connection files) in
`%LOCALAPPDATA%\Backspace\engine`, rebuilt when missing, and each computer's key
in `%LOCALAPPDATA%\Backspace\keys`. Your run history lives in
`Backspace.config` and travels with it.

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

When a Backspace update changes the recommended lists, what happens depends
on the backup's **Keep this backup's lists up to date automatically**
box (Filters → Recommended, under the groups):

- **On** (every backup set up only in Easy mode): when Backspace starts, and
  when its background service starts, each new or changed rule of a group the
  backup ticked is added to its rules, and a rule no longer recommended is
  removed, without asking. Clicking **Advanced** turns the box off; you can
  tick it again there.
- **Off** (backups set up or edited in Advanced mode): the backup keeps its
  rules, and its card says **New filter suggestions: Review** when changes
  concern a group it ticked. **Review** opens Filters → Recommended at **New
  filter suggestions**: each rule with its group and what it changes in this
  backup, measured on its folders ("Leaves out 2 files (41.0 KB) of this
  backup", "Matches nothing here today", or for a removal "Removing it backs up
  …"). Suggestions also cover rules missing from the backup's block (one you
  deleted is offered again) and rules that can't match in the backup's folders
  (offered again if you add a whole drive).

**Use ticked suggestions** puts the ticked suggestions in the rules (Save keeps them, like any change); the unticked ones are
hidden for that backup, and **Hide all** hides them all. A hidden suggestion
stays hidden: neither automatic updates nor ticking its group again
bring it back. Hidden suggestions are counted below the list; **Show hidden
suggestions** lists them again, unticked. A hidden suggestion that Backspace
no longer recommends is dropped, so it never comes back.

## Command line

```
Backspace                          # open the app
Backspace --background             # start in the tray only (used at sign-in)
Backspace --run <backup-name>      # run one backup now, without the app
Backspace --run-all                # run every enabled backup
Backspace --prune <backup-name>    # apply the backup's retention policy
Backspace --check <backup-name>    # run every check of the backup, one after another
Backspace --migrate <path>         # import an existing Duplicacy setup
Backspace --version
Backspace --help
```

They print what they do and their result in the Command Prompt or
PowerShell window they were started from. Backspace is a Windows program, so
Command Prompt shows its prompt again at once and the result follows a moment
later; to wait for it, use `start /wait Backspace --check <backup-name>`. In
PowerShell, add `| Out-Host` to wait.

While the background service runs a command, the window it was started from shows what it does: each check or backup as a heading, its steps and its result, then its summary.

With the background service set up, these commands run in it. After **Quit**
they still run: the service starts for that command only, keeps scheduled
backups stopped and stops again when the command is done, so Quit still holds.
If the service can't start, the command says so and what to do, naming the
command (backup, check or cleanup).
