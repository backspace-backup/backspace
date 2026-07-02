# Upstream: Backrest single-file restore on Windows for names with `[` or `]`

> **Status (2026-06-30):** Filed and **merged** as
> [garethgeorge/backrest#1254](https://github.com/garethgeorge/backrest/pull/1254)
> (merge commit `91d76ac`). Not yet in a Backrest release — latest is
> v1.13.0, and the fix is 28 commits ahead of that tag. This fix lives in
> Backrest's `/v1.Backrest/Restore` RPC, which Backspace does **not** use
> (our restore builds a raw `restic restore` invocation and runs it via
> `/v1.Backrest/RunCommand`), so it does not change Backspace's own
> behavior — it only helps other Backrest users. Retiring Backspace's
> `?`-substitution + post-restore sweep is gated on restic
> [`--include-from-raw` (#21872)](https://github.com/restic/restic/pull/21872),
> not on this PR.

## TL;DR

`RepoOrchestrator.Restore` in `internal/orchestrator/repo/repo.go:457`
passes a glob-bearing leaf name to `restic --include` on Windows without
escaping ("escaping is not supported on Windows") and without any
fallback, so Backrest's `/v1.Backrest/Restore` RPC silently restores
zero files when the user picks a single file whose name contains `[`
or `]`.

The fix is small (about 15 LOC) and uses restic's documented
`snapshotID:subfolder` syntax to bypass the glob-pattern matcher when
the leaf name has glob metachars on Windows.

## The bug, exactly

```go
// internal/orchestrator/repo/repo.go (current)
func (r *RepoOrchestrator) Restore(ctx ..., snapshotId, snapshotPath, target ...) {
    ...
    if snapshotPath != "" {
        normalizedPath := strings.ReplaceAll(snapshotPath, "\\", "/")
        dir := path.Dir(normalizedPath)
        base := path.Base(normalizedPath)

        if dir != "" {
            snapshotId = snapshotId + ":" + dir
        }
        if base != "" {
            opts = append(opts, restic.WithFlags("--include", "/"+escapeGlob(base)))
        }
    }
    ...
}

var globEscapeReplacer = strings.NewReplacer(`\`, `\\`, `*`, `\*`, `?`, `\?`, `[`, `\[`, `]`, `\]`)

func escapeGlob(s string) string {
    if runtime.GOOS == "windows" {
        return s // escaping is not supported on Windows
    }
    return globEscapeReplacer.Replace(s)
}
```

When `snapshotPath = /home/user/Q3 [draft].pdf` and `runtime.GOOS ==
"windows"`:

1. `dir = /home/user`, `base = Q3 [draft].pdf`
2. `escapeGlob(base)` returns `Q3 [draft].pdf` unchanged (the explicit
   "no escape on Windows" branch).
3. The restic invocation becomes `restore <snap>:/home/user --include
   "/Q3 [draft].pdf" --target T`.
4. Restic's `--include` uses Go's `filepath.Match`, which interprets
   `[draft]` as a character class matching any of `{d,r,a,f,t}`. The
   literal filename `Q3 [draft].pdf` doesn't match.
5. Zero files restored. The RPC reports success.

The comment "escaping is not supported on Windows" is correct about
the backslash escape but doesn't describe the consequence: on Windows
there is currently no path for Backrest to restore a single file with
brackets in its name. The user's selection is silently dropped.

## Why this is hard at the restic layer

Go's `filepath.Match` on Windows treats `\` as a path separator
(documented), so backslash escape isn't available. The POSIX-glob
class-self-escape trick (`[[]` for literal `[`, `[]]` for literal `]`)
is also rejected — restic's `ValidatePatterns` calls
`filepath.Match(pattern, pattern)` as a syntax check, and Go's matcher
returns `ErrBadPattern` for `[[]` (the `[` inside a class isn't a
valid character-range entry).

This has been raised on restic multiple times:

- [restic#3658](https://github.com/restic/restic/issues/3658) —
  "Restic won't find/restore files using --include containing square
  brackets in name (Windows)" — closed `not planned`
- [restic#2448](https://github.com/restic/restic/issues/2448) —
  "Paths in --files-from files that include brackets aren't backed
  up" — closed
- [restic#3005](https://github.com/restic/restic/issues/3005) —
  "Don't treat entries in --files-from as glob definitions" — closed
- [restic#2276](https://github.com/restic/restic/issues/2276) —
  bracket pattern failure — closed

Upstream is firm on inheriting `filepath.Match`'s behavior verbatim.
So a fix has to live in Backrest's restore-command builder, not in
restic.

## Proposed fix

On Windows, when the leaf name contains `[` or `]`, substitute each
bracket with `?` (Go's single-char wildcard). NTFS does not allow
`*` or `?` in filenames, so the wildcard never collides with a real
filename — but it does match any same-length sibling, so the caller
must sweep over-matched files from the target after restore.
Compared to dropping `--include` entirely (which over-restores the
whole parent), `?` substitution narrows the over-fetch to only
same-length siblings — typically zero, occasionally one or two —
keeping `restic restore`'s metadata fidelity (timestamps, ACLs,
EAs) intact along the way.

```go
var globEscapeReplacer = strings.NewReplacer(`\`, `\\`, `*`, `\*`, `?`, `\?`, `[`, `\[`, `]`, `\]`)
var windowsBracketReplacer = strings.NewReplacer(`[`, `?`, `]`, `?`)

func escapeGlob(s string) string {
    if runtime.GOOS == "windows" {
        // Backslash escape is unavailable on Windows (filepath.Match
        // treats \ as path separator). Class self-escape `[[]` is
        // rejected by ValidatePatterns. Substitute brackets with the
        // single-char wildcard `?` — NTFS forbids `?` in real
        // filenames so the wildcard cannot collide with a sibling
        // named exactly like the requested file. Same-length
        // siblings may over-match; callers should sweep the target
        // after restore.
        return windowsBracketReplacer.Replace(s)
    }
    return globEscapeReplacer.Replace(s)
}
```

Trade-off: on Windows, a single-file restore of a file with `[` or
`]` in its name may restore up to a handful of same-length sibling
files into the target. The current behavior — silently restoring
nothing — is strictly worse. If Backrest can post-sweep based on
the requested path, the over-restore becomes invisible to the user.

## Test coverage

A regression test under `internal/orchestrator/repo/repo_test.go`
covering the Windows-bracket case (guarded by `runtime.GOOS ==
"windows"`):

```go
func TestRestore_WindowsBracketLeafFallsBackToParentRestore(t *testing.T) {
    if runtime.GOOS != "windows" { t.Skip("Windows-only path") }
    // Setup: small repo with a snapshot containing
    //   /src/plain.txt
    //   /src/Q3 [draft].pdf
    // Restore RPC with path="/src/Q3 [draft].pdf"
    // Expect: target contains BOTH plain.txt AND Q3 [draft].pdf
    // (over-restore is acceptable; silent zero-restore is not).
}
```

## Why I filed this

I'm a maintainer of Backspace, a desktop GUI that wraps Backrest as
its restic orchestrator on Windows / macOS / Linux. The E2E matrix
caught this on Windows when a synthetic source file with brackets
in its name silently dropped from a partial restore. Working around
it in Backspace is straightforward (we use the `snapshotID:subfolder`
syntax directly via `RunCommand` instead of `/v1.Backrest/Restore`),
but the same workaround in Backrest itself would help every
Backrest user on Windows.

Filed and merged as [garethgeorge/backrest#1254](https://github.com/garethgeorge/backrest/pull/1254)
(2026-06-30).
