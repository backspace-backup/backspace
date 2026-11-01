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

**Calm and clear.** Porcelain: a cool near-white canvas behind white cards that
float on soft shadows, a confident deep-blue accent,
distinctive sans-serif headings, and enough space to read without wasting the
screen. The working controls stay familiar and easy to find. Colour is kept
for status and for the one primary action; secondary actions are filled
neutral tones, never outlines (29 October 2026, U034).

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
| `BS.Brush.BgBase` | `#F4F5F7` | Cool window canvas and content panes |
| `BS.Brush.BgSurface` | `#FFFFFF` | White cards and content |
| `BS.Brush.Sidebar` | `#EBEDF0` | Navigation panel, with a faint wash of the accent |
| `BS.Brush.BgElevated` | `#FFFFFF` | Dialogs, popovers |
| `BS.Brush.FgPrimary` | `#16181D` | Body text (ink, never pure black) |
| `BS.Brush.FgSecondary` | `#454A54` | Labels, metadata |
| `BS.Brush.FgDim` | `#646A75` | Captions, timestamps (4.64:1 on the sidebar) |
| `BS.Brush.Success` | `#15803D` | Healthy run status, encouragement |
| `BS.Brush.Warn` | `#B45309` | Skipped / attention-needed (amber-700) |
| `BS.Brush.Danger` | `#B91C1C` | Failed run, destructive actions (red-700) |
| `BS.Brush.ButtonTonal` | `#EEF0F3` | Secondary buttons and the count badge (hover `#E4E7EB`) |
| `BS.Brush.CardBorder` | 6% black | The near-invisible edge of a card; depth comes from its shadow |
| `BS.Brush.Band{Ok,Warn,Fail,Idle}{Bg,Fg,Icon}` | see `Theme.axaml` | Status bands and notices: tinted background, tone label, tone icon |

### Dark theme (opt-in)

| Token | Hex |
|---|---|
| `BS.Brush.BgBase` | `#121316` |
| `BS.Brush.BgSurface` | `#1A1B1F` |
| `BS.Brush.Sidebar` | `#0E0F12` |
| `BS.Brush.FgPrimary` | `#EDEEF0` |
| `BS.Brush.FgSecondary` | `#B4B8C0` |

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

Four font tokens have distinct roles. Manrope is bundled as static SemiBold
and Bold faces, so the wordmark and page titles do not depend on installed
fonts. Body text and controls use native Windows families. The older Fraunces
variable asset is retained but is not selected by the current design.

| Role | Family | Notes |
|---|---|---|
| Large page headings and wordmark | **Manrope** | Bundled static Bold face; open shapes and a compact, distinct silhouette. |
| Card and section headings | **Segoe UI Variable Display** | Native sans serif; retains the familiar hierarchy inside working surfaces. |
| Body and controls | **Segoe UI Variable Text** | Segoe UI fallback; unchanged native text rendering for labels, paths and inputs. |
| Monospace | **Cascadia Mono** | Consolas / monospace fallback for log lines and identifiers. |

Scale (14px body):

- Hero — Manrope, 36px / 48px line height, bold
- Page title — Manrope, 26px / 34px line height, bold
- Wordmark — Manrope, 19px / 26px line height, bold
- Page subtitle — native text sans, 13px / 18px, `FgDim`: one quiet line
- Card name — native display sans, 18px / 24px, semibold, beside a 36px accent icon tile
- Section title — native display sans, 16px / 24px, semibold
- Body — native text sans, 14px / 22px
- Label — native text sans, 11–12px, semibold, with light tracking
- Mono — Cascadia Mono, 13px / 20px

The Manrope files use the SIL Open Font License included as
`Assets/Fonts/Manrope-OFL.txt`. They come from the open-source v4 upstream
revision `6f81ebecdf65e4463b798cc07b16a4f8d5216917` in
[aaronbell/manrope](https://github.com/aaronbell/manrope/tree/6f81ebecdf65e4463b798cc07b16a4f8d5216917),
as recorded by [Google Fonts](https://github.com/google/fonts/blob/main/ofl/manrope/upstream_info.md).

## Shape language

- **Corners**: 14px on backup cards, 12px on status bands and notices, 8px on buttons, fully round on pills. Never sharp 0.
- **Borders**: no outlines on cards or buttons. Cards get depth from a soft two-layer shadow and a 6% edge; emphasis comes from fill, not strokes. `BorderEmphasized` is for inputs only; `BorderSubtle` for dividers.
- **Space**: protect readable text and reachable controls first. Main pages share 32px side margins; cards use tighter internal grouping. Small laptop work areas and Windows scaling determine how much room is actually available.
- **Custom title bar** on Windows: 32px high, matching the caption buttons. The sidebar runs to the top edge (its masthead is draggable) and page content starts right below the caption row, so the page title sits about 36px from the top (29 October 2026, U030).

## Iconography

Lucide-style **stroked path icons** at 1.6-2px stroke width, rounded
caps and joins. Defined in `Themes/Icons.axaml` and referenced by key
(`{StaticResource Icon.FolderOpen}` etc.). Always inherit `Foreground`
from the surrounding text so they re-color with the theme. Every icon is
drawn on its full 24-unit canvas, so its designed centre is the icon's
centre: symbols sit centred in notice discs and card tiles without
per-icon nudges (30 October 2026, U037). Keep new geometry inside 0-24.

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
- **Skipped** (e.g. external drive not mounted): orange tint, like a
  warning, so the band background and its icon agree (29 October 2026, U035).
- **Warning** (something off, not fatal): `Warn` dot + one sentence
  explaining what was off.
- **Notices** (`Border.notice`) are compact borderless strips whose whole
  background carries the tone, with a solid round glyph in the tone colour:
  orange for warnings and skips, red for failures, green for all-clear,
  accent tint for a recommended choice. Backup pills on a notice are raised
  white chips that scroll to and briefly outline that backup's card (U036). White surfaces hold
  content, never alerts; a white box with a small warning icon reads as
  unimportant (29 October 2026, U028). A notice is one line where it fits:
  headline, short detail, one action. Headline and detail are runs of one
  text line so they share a baseline (U038). A short phrase such as
  "Last activity: 21h ago" is never split: it stays beside the headline or
  moves whole to a second line. Headlines are status labels, so they take
  no full stop; the detail keeps full stops only between sentences.
- **Failed**: `Danger` dot + "Here's what happened" line. No red flash,
  no scary icon.

## Page headers

Main list pages put the title and a count badge on one row, with one quiet
subtitle line underneath and notices below that. No separate rule or toolbar
row: the list should start in the top third of the window (29 October 2026,
U030). The primary action (`Button.header-action`) stands to the right as
tall as title and subtitle together; the header text wraps 20px before it
rather than running underneath (30 October 2026, U040). The count badge
sits on the title's optical middle, halfway between the centre of its
capitals and of its lowercase letters, not on its line box (U039).

## Backup cards

The backup name leads the card, with its next run beneath it. One **Activity**
button opens runs and checks together; filters inside that window let the user
dig deeper without choosing between two competing history buttons.

Last backup and backup health share a status band of two tinted halves split by
a 2px gap, with no borders. Every outcome tints its half: green, orange
(warning, skipped, stale), red, accent (running) or neutral (never run). The
small label takes the tone colour; the value stays in ink. The health
result says what was established, such as **Backup records intact** or **Full
recovery tested**, rather than an unexplained “Check passed.” Color reinforces
the text; a shallow success never conceals an unresolved deeper failure. Source
paths sit in tinted pills. They keep their first 12 characters and as much of
the end as fits, shortening the middle only when needed. The complete path is
always available in the tooltip and accessible text. Both status columns use
the same success, warning and failure tints, with one meaningful icon per result.
Local destination paths use the same middle shortening as sources; cloud
destinations retain their names and provider icons. Titles leave room for descenders.

Run, mount, restore and Test restore remain visible. **More** contains settings,
the browser view and **Remove backup**, which also deletes what the backup stored. Those actions disable if a run
starts while the menu is open. A running comparison offers **View test restore**.
The action row wraps on narrow cards; labels and targets stay readable. Run now is the
only accent button; Mount, Restore files and Test restore are filled neutral
buttons; Activity and More are quiet text buttons that fill on hover.

Scheduled check choices use a comparison table for the catalog check, backup-data
sample, test restore, and both deeper checks. The selected row explains what it
can find and what it cannot prove. Recovery sampling controls appear only when
relevant. Selecting both explicitly explains the overlapping downloads; reopening
an older configuration never silently reduces its protection.

## Sidebar

The active item is a raised white chip with an accent icon, not a blue wash,
so blue keeps meaning "you can act here". Unselected items use secondary text
colour and fill with a neutral tone on hover.

Settings and Activity & logs sit just below the primary routes after a gap,
without rules. Version and provenance remain at the bottom. Above them, while
files in use may be skipped (or access needs one more approval), an orange
badge says so; clicking it explains and offers the fix. Nothing shows once
access is on, and Settings keeps Administrator access as a collapsed section
that opens by itself when something needs doing (29 October 2026, U027).

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
