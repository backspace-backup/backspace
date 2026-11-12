# Backspace's Windows restic build

On Windows, Backspace runs restic built from the official release named in
`src/Backspace/engine-versions.json` (`restic`), with the patches in `patches/`
applied. Its version reads `<version>-backspace.<n>` (`resticBackspace`), for
example `restic 0.19.1-backspace.1`. Other platforms use the official restic
release unchanged.

## Why

`0001-filter-skip-patterns-a-path-cannot-match.patch` makes exclusion matching
20-40 times faster with the same result: before running restic's match, it
checks that the path contains the plain text a rule needs (a folder name such
as `node_modules`, or `.tmp` for `*.tmp`). Restic matches every rule against
every path, twice per backup, on every run; with local_c's 273 rules this was
about 300 s of CPU for 743,862 paths, and 7 s with the patch, with no path
excluded differently (U137, 4 November 2026). It is proposed upstream as
https://github.com/restic/restic/pull/22165.

`0002-filter-let-a-later-double-wildcard-match-nothing.patch` fixes a rule
with two or more `**`: restic tried the first one on too few folders, so
`C:/**/*/**/node_modules/*` left `C:/a/node_modules/x` in
(https://github.com/restic/restic/issues/22131). It backports the upstream
fix https://github.com/restic/restic/pull/22134 (U151, 7 November 2026).
Backspace's Simulate, Count and estimates follow the fixed matching on Windows
and the official one elsewhere.

`0003-fs-treat-offline-and-recall-on-open-files-as-online-only.patch` makes
`--exclude-cloud-files` skip Dropbox's online-only files quietly (U168,
9 November 2026). Official restic skips only files marked
FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS. Most processes see Cloud Files
placeholders without it: a Dropbox online-only file reads 0x1620 (ARCHIVE |
SPARSE_FILE | REPARSE_POINT | OFFLINE), and restic reported
`unsupported file type "irregular"` for each one and ended the run as a partial
backup (58,335 such files in the owner's Dropbox). With the patch, a file (not
a folder) marked FILE_ATTRIBUTE_OFFLINE or FILE_ATTRIBUTE_RECALL_ON_OPEN is
online-only too. Only the attributes Lstat already read are used (it opens a
reparse point with FILE_FLAG_OPEN_REPARSE_POINT and no data access), and the
check runs before restic looks at the file's type. The Cloud Files reparse tag
alone is not used: a downloaded placeholder keeps it, and its data is local.
Its tests cover the attribute values and a local file marked offline.
Upstream: the irregular-file reports are
https://github.com/restic/restic/issues/4704 (closed, Dropbox files among
them); `--exclude-cloud-files` came from
https://github.com/restic/restic/pull/4990 (for
https://github.com/restic/restic/issues/4155) and checks RECALL_ON_DATA_ACCESS
only. No upstream issue or pull request about OFFLINE placeholders was found
on 9 November 2026; this change is not proposed upstream yet.

`0004-fs-restore-without-saved-security-descriptors-when-asked.patch` lets a
restore leave out the saved Windows security descriptors when
`RESTIC_RESTORE_SKIP_SECURITY_DESCRIPTORS=1`: restored files then take the
permissions of the folder they are restored into, like new files (U219,
12 November 2026). Backspace sets it only for test restores, whose copies
are read and then deleted. restic restores each file's saved descriptor
(only its DACL without administrator rights), and a test restore of a whole
drive brought back copies of folders such as `ProgramDataMicrosoftWindowsWindowsApps`
and `CryptoRSAMachineKeys` granting only SYSTEM, administrators or
TrustedInstaller: the owner's account, Dropbox and Backspace's cleanup could
neither read nor delete them. A restore of your files keeps the saved
permissions. Its test restores a file and a folder with each test descriptor
and checks they get the descriptor of a new item in the same folder.
Upstream restic has no option to skip them; not proposed upstream yet.

## When to remove it

When a restic release contains all four changes (or, for 0004, an option to
restore without the saved security descriptors): set `restic` to that release,
delete `resticBackspace` and the patches, and Windows uses the official release
again. Until then, a restic upgrade means rebasing the patches onto the new
tag, running the filter, fs and archiver tests, building a new
`-backspace.<n>` and checking it as any engine upgrade (docs/GOTCHAS.md,
"Engine upgrades require a compatibility check"). A new build also needs
`src/Backspace/Assets/restic.exe` deleted locally, so the next build fetches it.

## How it is built and fetched

`build.ps1` clones the official tag from github.com/restic/restic, applies the
patches, sets the version, runs restic's filter, fs and archiver tests (they
need an elevated runner: symbolic links, shadow copies) and builds with
`go build -trimpath -ldflags '-s -w'` (CGO off). The workflow
`.github/workflows/build-restic.yml` runs it and publishes
`restic_<version>_windows_amd64.zip` (plus its SHA-256 and the patches) as the
GitHub release `engine-restic-<resticBackspace>` of this repository.

- Builds: `src/Backspace/fetch-engines.ps1` downloads that asset.
- Installed copies: `EngineInstallation` downloads it into
  `%LOCALAPPDATA%\Backspace\bin` when missing, checked against the SHA-256 built
  into Backspace.exe, as for the other engines.

They are published as prereleases, so the update check (which reads the
latest release) never sees them. The `engine-restic-*` releases must stay published: Backspace versions that
use a build fetch it from there. Never delete them when cleaning up releases.
