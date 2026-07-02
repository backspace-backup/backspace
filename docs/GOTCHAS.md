# GOTCHAS.md — known sharp edges

Upstream-driven traps and platform-specific surprises that aren't
obvious from the code. Read before touching the related subsystem.

## OAuth first-run flow (Dropbox / OneDrive / Google Drive)

`OAuthFlowCoordinator` runs a full PKCE round-trip on a localhost
loopback listener. The user never copy-pastes a token. If the provider's
authorize-URL contract changes (Dropbox has a habit of this every couple
of years), the flow returns `OAuthFlowResult.Failed` with a
human-readable reason — it never surfaces a half-validated token.

`DropboxAuthProbe` does an `rclone lsd` round-trip against a per-flow
temp `rclone.conf` before save so a stale or scope-mismatched token
fails loudly.

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

## WebDAV-mounted snapshot browsing is Windows-only today

The service compiles on Unix but `SnapshotMountCoordinator.MountAsync`
is gated on `OperatingSystem.IsWindows`. On macOS / Linux the matching
path will need a FUSE-backed implementation.

## Healthchecks.io management API rate limits

We batch settings updates. Adding 50 backups in a loop may hit 429s; we
retry with backoff.

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

Workaround inside `ResticBackrestEngine.RestoreFilesAsync`: substitute
each `[` and `]` with `?` (Go's single-char wildcard) when any
requested file has glob metachars in its leaf name; NTFS forbids `?` in
real filenames, so the wildcard cannot collide with a sibling whose
name is exactly the requested file. After the restic call returns, the
engine sweeps anything outside the requested set so the user sees only
the files they asked for.

Upstream status:
[garethgeorge/backrest#1254](https://github.com/garethgeorge/backrest/pull/1254)
(the same `?`-substitution in Backrest's `escapeGlob`) **merged
2026-06-30**, but it is not yet in a Backrest release (latest is
v1.13.0) — and it does **not** affect Backspace: our restore builds a
raw `restic restore` invocation and runs it via `/v1.Backrest/RunCommand`,
so Backrest's `escapeGlob` (which only runs in the `/v1.Backrest/Restore`
RPC we don't use) never executes for us. The actual retirement gate for
the in-process `?`-substitution + sweep is
[restic/restic#21872](https://github.com/restic/restic/pull/21872)
(adds `--include-from-raw` for literal-path includes, still open): once
it ships in a restic release we bundle, the substitution + sweep can be
replaced with a literal include. Until then the in-process workaround
stays load-bearing. See [ROADMAP.md](ROADMAP.md).
