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

## When to remove it

When a restic release contains that change: set `restic` to that release,
delete `resticBackspace` and the patch, and Windows uses the official release
again. Until then, a restic upgrade means rebasing the patch onto the new tag,
running the filter tests, building a new `-backspace.<n>` and checking it as
any engine upgrade (docs/GOTCHAS.md, "Engine upgrades require a compatibility
check").

## How it is built and fetched

`build.ps1` clones the official tag from github.com/restic/restic, applies the
patches, sets the version, runs restic's filter tests and builds with
`go build -trimpath -ldflags '-s -w'` (CGO off). The workflow
`.github/workflows/build-restic.yml` runs it and publishes
`restic_<version>_windows_amd64.zip` (plus its SHA-256 and the patches) as the
GitHub release `engine-restic-<resticBackspace>` of this repository.

- Builds: `src/Backspace/fetch-backrest.ps1` downloads that asset.
- Installed copies: `EngineInstallation` downloads it into
  `%LOCALAPPDATA%\Backspace\bin` when missing, checked against the SHA-256 built
  into Backspace.exe, as for the other engines.

They are published as prereleases, so the update check (which reads the
latest release) never sees them. The `engine-restic-*` releases must stay published: Backspace versions that
use a build fetch it from there. Never delete them when cleaning up releases.
