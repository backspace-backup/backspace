# DESIGN.md — visual direction for Backspace

> A backup app for the person who just wants to know their stuff is safe.
> Not a dashboard. Not a console. A kind, considered home for their data.

The goal: when a non-technical friend opens Backspace for the first
time, they exhale. *"Oh. I can do this."*

The source of truth for tokens is `src/Backspace/Themes/`. This
file explains the **intent** behind the values there — what each token
is for, what the typography is doing, what the motion is meant to feel
like. When the code and this file disagree, the code wins.

## Aesthetic

**Calm editorial.** Warm paper behind white cards, a confident deep-blue accent,
generous whitespace, serif headings. Think "well-designed journal app"
rather than "system utility". Restrained, not flashy.

What we're not:

- not cute, playful, or mascot-driven
- no emoji as punctuation
- no celebrating with confetti
- no rust-red failure indicators or fire-alarm icons
- no purple-to-pink gradients, no generic SaaS blue gradients
- no pretending backups are fun — we make them *feel cared for*

## Palette

Tokens are defined in `Themes/Theme.axaml` (base) + one
`Themes/Accent_<Name>.axaml` per accent (`Ocean`, `Emerald`, `Plum`,
`Graphite`, `Terracotta` — picked in Settings; the active accent
drives primary buttons, the sidebar's selected-item highlight, focus
rings, and the taskbar icon). Control styles built on top of these
tokens live in `Themes/Controls.axaml`; the stroked icon set lives in
`Themes/Icons.axaml` and is referenced as
`{StaticResource Icon.<Name>}`.

The values below match the current theme; if you change them, change
both this doc and the `.axaml` together.

### Light theme (default)

| Token | Hex | Use |
|---|---|---|
| `BS.Brush.BgBase` | `#F6F5F1` | Warm window canvas and content panes |
| `BS.Brush.BgSurface` | `#FFFFFF` | White cards and banners |
| `BS.Brush.Sidebar` | `#EEECE5` | Warm navigation panel |
| `BS.Brush.BgElevated` | `#FFFFFF` | Dialogs, popovers |
| `BS.Brush.FgPrimary` | `#1A1915` | Body text (inky warm black, never pure black) |
| `BS.Brush.FgSecondary` | `#4A4740` | Labels, metadata (8.49:1 on the canvas) |
| `BS.Brush.FgDim` | `#6A665C` | Captions, timestamps (5.25:1 on the canvas; 4.84:1 on the sidebar) |
| `BS.Brush.Success` | `#15803D` | Healthy run status, encouragement |
| `BS.Brush.Warn` | `#B45309` | Skipped / attention-needed (amber-700) |
| `BS.Brush.Danger` | `#B91C1C` | Failed run, destructive actions (red-700) |

### Dark theme (opt-in)

| Token | Hex |
|---|---|
| `BS.Brush.BgBase` | `#141414` |
| `BS.Brush.BgSurface` | `#1C1C1C` |
| `BS.Brush.Sidebar` | `#181818` |
| `BS.Brush.FgPrimary` | `#F2F1EE` |
| `BS.Brush.FgSecondary` | `#BFBBB2` |

Dark tints (`SuccessTint`, `WarnTint`, `DangerTint`) are slightly
brightened so the AA contrast against the dark base holds.

### Accents

Five options, picked in Settings. The active accent drives primary
buttons, the sidebar's selected-item highlight, status focus rings, and
the taskbar icon.

| Slug | Hex | Mood |
|---|---|---|
| **Ocean** *(default)* | `#0061FF` | Deep saturated blue. Confident, trustworthy. |
| Emerald | green | Soft, growing, calm |
| Plum | deep purple-pink | Editorial, distinctive |
| Graphite | warm dark grey | Quiet, restrained |
| Terracotta | warm clay-orange | Cozy, kitchen-table |

WCAG AA contrast against `BgBase` and `BgSurface` is verified by
`tests/Backspace.Tests/Layout/ContrastTests.cs` — if you tweak the
palette, that test will tell you immediately if a pair drops below 4.5:1.

## Typography

Four font tokens have distinct roles. Native installed fonts are used;
the bundled Fraunces variable asset is not selected by this proposal.

| Role | Family | Notes |
|---|---|---|
| Large page headings and wordmark | **Georgia** | Editorial serif; generic serif fallback outside Windows. Normal weight keeps the forms open. |
| Card and section headings | **Segoe UI Variable Display** | Native sans serif; retains the familiar hierarchy inside working surfaces. |
| Body and controls | **Segoe UI Variable Text** | Segoe UI fallback; unchanged native text rendering for labels, paths and inputs. |
| Monospace | **Cascadia Mono** | Consolas / monospace fallback for log lines and identifiers. |

Scale (14px body):

- Hero — Georgia, 40px / 56px line height, normal weight
- Page title — Georgia, 32px / 44px line height, normal weight
- Wordmark — Georgia, 22px / 30px line height
- Card name — native display sans, 22px / 28px, semibold
- Section title — native display sans, 16px / 24px, semibold
- Body — native text sans, 14px / 22px
- Label — native text sans, 11–12px, semibold, with light tracking
- Mono — Cascadia Mono, 13px / 20px

## Shape language

- **Corners**: 10-12px on panels and primary buttons, 6-8px on inputs and small chips. Never sharp 0; never overly round.
- **Borders**: single hairline (`BS.Brush.BorderSubtle`). Shadows stay shallow and faint on cards; floating dialogs can use a softer, larger shadow.
- **Space**: generous. Prefer wider margins to tighter rows. The app is single-user, single-screen — we have the room.
- **Custom title bar** on Windows: 32px high; its left segment matches the sidebar and its right segment matches the canvas. The app owns the chrome.

## Iconography

Lucide-style **stroked path icons** at 1.6-2px stroke width, rounded
caps and joins. Defined in `Themes/Icons.axaml` and referenced by key
(`{StaticResource Icon.FolderOpen}` etc.). Always inherit `Foreground`
from the surrounding text so they re-color with the theme.

The app icon — a cloud with a check inside, on an accent-tinted
rounded-square tile — is pre-rendered per accent and per syncing frame.
See [docs/icon.md](docs/icon.md) for the build pipeline.

## Copy tone

Friendly, concrete, no jargon. Default to helping rather than describing.

| Don't | Do |
|---|---|
| "No backups yet" | "Let's set up your first backup" |
| "Destination configuration" | "Where should backups live?" |
| "Execution failed" | "Something didn't work — here's what happened" |
| "Schedule" | "When should this run?" |
| Confirm subtitle: "Irreversible" | "Existing files under this folder will be replaced" |
| "Metered network detected, backup aborted" | "Paused — we noticed you're on a metered connection" |

Button labels are verbs that describe what happens next: "Save changes",
"Run now", "Restore these files" — not "OK", "Apply", "Submit".

## Motion

Calm and purposeful. No bounce, no spring, no scale-on-hover.

- View transitions: short fade + 4px translate-up on the incoming view.
- Hover states: ~150ms ease-out on color and background. Never on size.
- Backup in progress: a slow horizontal progress bar + an animated
  syncing icon on the taskbar (4 pre-rendered frames cycling at ~160ms,
  brisk cadence — fast enough to feel alive, slow enough to read).
- Success: a soft check-fade-in. No bounce.
- Failure: the row border tints `Danger` and holds. No shake, no flash.
  A plain-English "here's what happened" line below.

## Status semantics

- **OK / healthy**: small `Success` dot, no other ornament.
- **Running right now**: a subtle animated indicator (taskbar icon
  cycles, in-app overall progress strip visible).
- **Skipped** (e.g. external drive not mounted): muted `FgDim` line. Not
  a failure — explicitly normal.
- **Warning** (something off, not fatal): `Warn` dot + one sentence
  explaining what was off.
- **Failed**: `Danger` dot + "Here's what happened" line. No red flash,
  no scary icon.

## Sidebar

A soft rounded-pill highlight on the active item — a gentle accent tint
filling the whole row, not a hard vertical bar. Unselected items use
secondary text color; on hover they pick up a 6% accent tint background.

Settings and Activity & logs sit just below the primary routes with a
small dividing gap. Version and provenance remain at the bottom.

Custom title bar on Windows (height matches the sidebar's top section)
so the app feels like one continuous surface.

## Empty states

Always include three things:

1. A short reassuring line of prose
2. One clear primary action
3. A one-sentence hint about what happens when they click

Example for the Backups page when there's nothing yet:

> **Let's set up your first backup.**
> Pick a folder you care about, tell us where it should live, and we'll
> keep it safe for you.
>
> [ Set up a backup ]

## Destructive moments

Type-to-confirm. Always.

- Title: "About to erase this backup's storage" (not "DANGER")
- Subtitle: "This removes every revision stored at this destination.
  Storage elsewhere is untouched. There's no undo."
- Confirm string: `OK` (case-insensitive), with the specific consequence
  explained in the dialog subtitle.
- Surrounding callout border: `Danger` outline, not a fire-red flash.

## What this isn't

- isn't cute, playful, or illustrated with mascots
- doesn't use emoji as punctuation
- doesn't celebrate with confetti
- doesn't hide technical details from users who want them (the
  Advanced editor surfaces every restic / Backrest knob)
- doesn't pretend backups are fun — makes them *feel cared for*
