# AGENTS.md — working conventions for agents

This file is the entry point for whoever picks up the codebase next,
human or AI. Read it top-to-bottom before making changes — it captures
the conventions that aren't obvious from the code alone, and points to
the deeper docs you'll need next.

## What the app is

A friendly desktop GUI for [restic](https://restic.net/) backups,
driven by a bundled [Backrest](https://github.com/garethgeorge/backrest)
daemon as the engine. Avalonia-based, single self-contained `.exe`,
portable config folder. Windows is the supported target; macOS / Linux
compile but the suite has not been exercised on either OS.

The user-facing story lives in [README.md](README.md). This file
focuses on contributor / agent concerns.

## Where to read deeper

| Topic | File |
|---|---|
| Code map: services, layout, storage, principles, invariants, CLI | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Visual identity: palette, type, motion, copy tone | [DESIGN.md](DESIGN.md) |
| Test suite + scenario harness + standing instruction | [docs/TESTING.md](docs/TESTING.md) |
| Issues, decisions and future work (local, Git-excluded) | [Review desk](http://127.0.0.1:4317/) and [planned work](http://127.0.0.1:4317/roadmap) |
| Sharp edges (OAuth, Backrest guid contract, WebDAV, bracket-restore) | [docs/GOTCHAS.md](docs/GOTCHAS.md) |
| Icon build pipeline (per-accent ICOs, syncing frames) | [docs/icon.md](docs/icon.md) |
| Scenario harness deep-dive (how to add a scenario) | [tests/Backspace.Tests/Scenarios/README.md](tests/Backspace.Tests/Scenarios/README.md) |
| PR conventions | [CONTRIBUTING.md](CONTRIBUTING.md) |

Build & test commands live in [README.md § Building from source](README.md#building-from-source)
and [docs/TESTING.md](docs/TESTING.md).

## The Review desk workflow (single source of truth)

The user requested one unified project board on 10 October 2026.
In this checkout, the **local Review desk is the source of truth** for
issues, planned work, comments, decisions and proof. Open
http://127.0.0.1:4317/ for active issues or /roadmap for planned work.
Read the desk when resuming work; the latest user comment takes precedence.
There is no Markdown mirror. Do not create or regenerate a second tracker.
Technical documentation describes app behavior, not issue status or queues.

When an upstream fix may replace local workaround code, keep its findings
in the existing Review desk issue. Document the removal
conditions in the relevant technical guide. A merged PR alone is not enough:
the fix must be released, bundled and tested before removing protections.
For restore bracket matching, see [the cleanup criteria](docs/GOTCHAS.md#reviewing-the-upstream-bracket-fix).

**Next-agent reminder — review Backrest PR #1411 by 12 January 2027.**
When resuming this project, compare the current date with this deadline and
check R007 for a recorded review. If the date has arrived or passed and the
review has not been done, check whether the PR has merged, whether a stable
release contains it, and whether Backspace bundles that release. Assess which
remaining local code can safely be simplified using the criteria above,
then record the findings and review date in R007. This is an instruction for
the next agent, not a ChatGPT task or other scheduled automation. Do not
create a scheduler job for it.

The dashboard and its JSON stay Git-excluded at the user's request.
Copy them separately when moving this checkout. Start the desk with
`Launch-Audit-Dashboard.bat`; its local README documents recovery and the API.
If it is unavailable, restore access to the existing desk before updating
work records. Report any access blocker rather than inventing another queue.
Never reseed or overwrite the saved JSON.
Use the dashboard API with the current revision/version; it preserves
comments, user decisions and earlier wording. Its local README documents
the API. Record agent replies as Agent, Codex or Claude; the queue label
is always **With Agent**.

Planned items use the same record as active issues. Next, Soon, Later
and Parked are planning stages, independent of testing status and whose
turn it is to reply. **Planned does not authorize implementation.**
Wait for the user's go-ahead or their explicit Start work action. B035
(online-only cloud sources) is specifically parked pending that approval.
Answer comments on planned items without silently activating them.

**The rules — non-negotiable:**

1. **Every bug report or feature request the user raises gets logged
   immediately as a record in the local desk.** Before doing
   anything else with the request: record its title, type,
   raised-on date and initial status. If the user
   raises five things in one message, that's five entries — don't
   collapse them. The point is to never silently drop or half-fix an
   item.

2. **Entries leave the active queue ONLY after the user explicitly validates
   them.** Status moves `Open` → `Awaiting validation` once you've
   shipped a fix or feature AND attached proof (screenshot path, test
   name + result, log excerpt, commit hash — whatever is appropriate
   for the item). Then the user reads the proof, confirms, and you
   record that validation in the desk and preserve its discussion and proof.
   The user's completion checkbox is also explicit validation.
   **Never mark an entry completed on your own.** A user "thanks" or "great" is not validation; an
   explicit "remove from roadmap" / "confirmed" / "validated" is.

3. **Proof must be what a reasonable human validator would actually
   open.** The user will not read source code unless something is
   genuinely broken — proof must be a visual artefact (screenshot,
   rendered report page) they can click and verify with their eyes
   in seconds. A few hard rules:

   - **🖼️ A screenshot is the default proof shape.** Every
     desk entry whose verdict is "look at the UI" must link
     directly to the PNG. The scenario suite writes these to
     `tests/Backspace.Tests/artifacts/scenarios/<name>/screenshots/`;
     point at the specific file, not at the index page.
   - **❌ Never reference source-code line numbers in proof
     blocks** (`RestoreViewModel.cs:245-282`, etc.). Line numbers
     go stale on the next edit and make the entry lie. The fix
     itself stays in git; the proof block exists for the user,
     not for code archaeology.
   - **❌ Never link to a source file at all unless the entry is
     a refactor whose deliverable IS the file structure.** For a
     bug fix or feature, the user does not want to read C# to
     confirm — they want to look at the screenshot.
   - **Describe what the screenshot SHOULD show**, in one line,
     so the user knows what to look for before opening it
     (e.g. "5 dated rows with the latest selected, Next enabled").
     Without that, the proof is a "here's a PNG, trust me" gesture.
   - **Test names + pass results** are acceptable secondary proof
     only when there is no UI artefact (e.g. wire-protocol fix,
     CLI argument parsing). Even then, link to the rendered
     scenario report page for the run — not the `.cs` file.

   Unacceptable in any form: "I checked, it works", "the code now
   does X", "should be fine". Attach proof to the desk entry's evidence
   with a plain-language description as soon as it's available —
   don't park it in a separate message.

4. **Address active work by priority** — P0 (production
   broken) at the top, then P1 (visible bugs), P2 (polish), P3
   (features). Within a tier, order by what blocks what.

5. **When the user adds context to an existing item** (clarifies,
   redirects, raises related concern), update the desk entry in
   place — don't create a duplicate. The entry's body is allowed to
   grow as long as the title stays the same.

6. **Stale desk items poison the workflow as much as stale docs.**
   If an item has been `Awaiting validation` for more than a week
   without user response, flag it in the next turn so the user can
   either validate or redirect.

**Issue self-containment.** Never write "see chat", "follow-up
question above", or any reference that assumes the chat history
persists. The desk is the durable record — every entry
must read cleanly with no out-of-band context. When user input is
needed on an entry, end it with a line that starts with **`I need
from you:`** followed by the exact ask. Keep its status and whose-turn
fields accurate so the desk's filters show the user's queue clearly.

7. **Adversarial self-review before claiming `Awaiting validation`.**
   For every batch of items you mark `Awaiting validation`, do an
   adversarial pass on YOUR OWN work BEFORE surfacing to the user.
   The pass is short — for each item ask:
   - **Did my test actually exercise the bug, or just the
     surface?** (e.g. a unit test on a label string is necessary but
     not sufficient when the user reported a UI behaviour bug —
     the integration is where it could still fail.)
   - **What edge case would silently regress this?** Name it. If
     it's likely, add a test. If it's unlikely, document why in the
     proof line.
   - **Is the proof FALSIFIABLE?** A passing test that runs the
     happy path doesn't prove correctness — assert the negative
     case too (the thing that SHOULDN'T match, the input that
     SHOULDN'T trigger the message).
   - **Does the fix re-create the user's exact starting state?**
     If they reported "I clicked X, saw Y", does your test click
     X (not just "exercise the underlying code")?
   Surface the adversarial findings in the same turn as the
   completion claim — either as inline test additions or as a
   clearly-flagged "limitations" paragraph under the Proof line.
   Never claim "shipped" silently when you know a gap exists.

   This rule exists because past sessions claimed several
   "Validated" items that the user later found still half-fixed.
   The cost of one extra round of self-skepticism is low; the cost
   of "this was claimed done but isn't" compounds across the
   project's lifetime.

**Entry content guide:** Store this information in the desk's fields;
this is not a template for a separate Markdown tracker. Use the current
API schema in the local README for status values and mutations.

```markdown
### <priority>: <one-line title>

- **Raised:** YYYY-MM-DD
- **Type:** bug | feature | refactor | coverage
- **Status:** Record implementation/testing status separately from whose turn it is.
- **Context:** What the user reported, plain.
- **Root cause + fix (short):** One paragraph max. Skip if it's
  obvious from the title.
- **Proof — open and look:**
  - 🖼️ **[link to screenshot PNG](relative/path/screenshot.png)** —
    one-line description of what's in the picture and why it
    proves the fix (e.g. "5 dated rows, latest selected, Next
    enabled — before the fix this was a blank surface").
- **I need from you:** One concrete action the user can take to
  confirm. End the entry here.
```

The desk's roadmap columns contain the **long-term backlog**, not active work. Move
an active item there only on explicit user direction; do not park an
unfinished fix merely to empty the agent queue. The 10 October migration
preserves the previous backlog and a record of stale or superseded claims.

## Working conventions

Before publishing any GitHub release, follow [docs/RELEASING.md](docs/RELEASING.md).
Complete the current release checklist for the exact committed revision,
including the real 20,000-file round trip, documentation review and current
screenshots. Save it as `release-checks/<full-commit>.json`; this folder stays
Git-ignored. Every required box needs actual work and specific findings.
Use `scripts/release-checklist.cjs` to validate and dispatch it to GitHub.
No human approval is required: the agent performs and records the review.
Never bypass the workflow or reuse a checklist after the commit or template
changes. Do not claim the gate is enforced until its repository settings
and both passing and rejecting workflow paths have actually been exercised.

These rules exist because they were learned the hard way during prior
sessions. Read them once and treat them as load-bearing.

1. **Commit after every round of substantive changes.** "Round" = a
   coherent group of edits that builds and passes tests. Don't let
   uncommitted work pile up across many turns — a single misguided
   `git checkout` or `rm` can wipe a week of in-flight changes that
   nobody can recover from reflog. WIP commits are fine; squash later
   if needed.
2. **Never `git checkout HEAD -- <file>` without confirming with the
   user first.** It silently destroys working-tree changes that aren't
   yet committed. Use `git stash` (recoverable) for any "revert just
   this file" need, or fix the issue in place. Same rule for
   `git reset --hard`, `git restore`, `git clean -f`, and
   `git checkout --`.
3. **PowerShell `Get-Content -Raw` defaults to Windows-1252.** That
   silently corrupts UTF-8 multi-byte characters (em-dash, bullet,
   curly quotes) — a write-back produces double-encoded mojibake that
   compiles but renders as `â€"` etc. Always use
   `[System.IO.File]::ReadAllText($path, [System.Text.UTF8Encoding]::new($false))`
   on any file with non-ASCII content. (Reverse a double-encode by
   re-encoding the broken string as Windows-1252, then decoding the
   resulting bytes as UTF-8.)
4. **Edit beats Write beats sed/perl for non-ASCII text files.** The
   `Edit` tool preserves surrounding bytes verbatim. `Write` replaces
   the whole file with whatever encoding the harness uses (UTF-8
   no-BOM, safe). PowerShell pipes through .NET strings, which
   round-trips fine as long as you read with the right encoding (see
   #3).
5. **When in doubt about a destructive action, ask.** Confirm before
   deletes, force-pushes, `rm -rf`, `git checkout --`,
   `restic forget`, `backrest --reset-db`, etc. The cost of pausing
   to confirm is low; the cost of an unwanted action (lost work,
   deleted branches, wiped repo) is unbounded.
6. **Before every commit, sweep markdown for staleness.** Run the
   pre-commit doc-check below. If anything fails, fix it in the same
   commit — stale docs poison every future read and the cost compounds.
   The repo has had multiple "Duplicacy still everywhere in AGENTS.md"
   surprises because past sessions only cleaned the code surface, not
   the prose.

## Pre-commit doc-check

For every `*.md` file that touches anything the current commit changes:

```bash
# 1. Anything you renamed / removed in code that the doc still mentions?
git diff --cached --name-only | xargs -I{} basename {} .cs | sort -u > /tmp/touched
grep -rhF -f /tmp/touched README.md AGENTS.md DESIGN.md docs/*.md \
  tests/Backspace.Tests/Scenarios/README.md 2>/dev/null | head -20

# 2. Engine vocabulary check — these words should ONLY appear in
#    legitimate Duplicacy-migration contexts (ConfigMigrator, the
#    Settings "Import from existing Duplicacy" section, the
#    --migrate CLI line, and the deferred copy-between-destinations
#    backlog item). Any other hit is stale.
#    Scope must include ROOT *.md and any nested README.md — earlier
#    versions of the check missed DESIGN.md (`Duplicacy CLI` in the
#    design principles) and tests/Backspace.Tests/Scenarios/README.md
#    (`Live duplicacy.exe runs`). Both shipped stale until round 5.
#    Excludes gitignored historical evidence in the local Review desk
#    (archived migration material may retain Duplicacy references).
for f in $(git ls-files '*.md'); do
  case "$f" in
    AGENTS.md) continue ;;  # AGENTS.md mentions Duplicacy in this very rule
  esac
  grep -inE "duplicacy|FetchDuplicacy|DuplicacyRunner|DuplicacyEmbedder" "$f" 2>/dev/null
done

# 3. Filename-existence check — every `Foo.cs` / `Foo.axaml` /
#    `Foo.targets` in backticks should still exist SOMEWHERE in the
#    tree (docs reference files relative to whatever subtree they're
#    describing; the test is "does the filename exist at all", not
#    "at this exact relative path").
grep -hoE '`[a-zA-Z0-9._/-]+\.(cs|axaml|targets|csproj|json|yml|ps1|bat|sh)`' \
  README.md AGENTS.md DESIGN.md docs/*.md 2>/dev/null \
  | tr -d '`' | sed 's|.*/||' | sort -u | while read fn; do
      [ -z "$(find . -name "$fn" -not -path "./bin/*" -not -path "./obj/*" -not -path "*/bin/*" -not -path "*/obj/*" -not -path "./.upstream-prs/*" -print -quit 2>/dev/null)" ] \
        && echo "STALE FILENAME: $fn"
    done

# 4. Build/CLI command check — every `dotnet build/test/publish`,
#    `-p:Skip*`, and `Backspace --flag` invocation in docs should
#    match what currently works.
grep -inE "dotnet (build|test|publish|run)|-p:Skip|Backspace --" \
  README.md AGENTS.md docs/*.md 2>/dev/null
# Eyeball: do these still match what `dotnet build src/Backspace -h`
# and Program.cs's argv parser accept?
```

The fastest way to keep docs honest is **the same commit that renames a
class also renames its doc mentions**. If the diff is too big for one
commit, at minimum add a TODO comment in the affected doc so the next
agent sees it. Stale docs that "we'll fix later" never get fixed —
they get believed.
