# Icon pipeline

The Backspace app icon is **cloud-check**: a soft, friendly cloud
with a check mark cut out of its body, on a rounded-square tile in
the current accent color. Reads as "safely stored in the sky" and
stays legible down to 16×16.

## Files

```
assets/brand/
  ├── backspace.svg              ← master design, 1024×1024, Ocean background
  ├── backspace-<size>.png     ← rasterized Ocean outputs at 16..1024 (generated)
  └── concepts/                     ← 36 alternative concepts (history + easy swap)

src/Backspace/Assets/
  ├── Backspace.ico                        ← default embedded icon (Ocean)
  └── Backspace-<Accent>.ico               ← cloud + check, per accent
```

Accents: `Ocean`, `Emerald`, `Plum`, `Graphite`, `Terracotta`: 5 ICOs
committed to the repo, ~6 KB each.

When the user changes the accent in Settings, `MainWindow` reloads the
matching ICO so the **taskbar, alt-tab, and window icon all re-color
with the accent**. The sidebar logo (inline in `MainWindow.axaml`)
tracks the accent directly via `DynamicResource BS.Brush.Accent`, so
it re-colors without reloading.

## While syncing

The window, taskbar and sidebar icons stay still while a backup or restore
runs (owner, 14 November 2026: the turning taskbar icon "looks like crap",
and "don't animate the Backspace logo of the top left"). The tray icon
and the running cards show the activity. The earlier rotated
`Syncing-<N>` frames were removed.

## How to regenerate the icon

```powershell
# Default single-file (Ocean) build — overwrites Backspace.ico
.\tools\build-icon.ps1

# Arbitrary color for experimentation
.\tools\build-icon.ps1 -Accent "#059669"

# Canonical build: all 5 accent ICOs + the default Ocean ICO.
.\tools\build-icon.ps1 -All
```

Run the `-All` variant whenever the design or the accent palette
changes. The script may still write syncing frames: don't commit them.

## How to change the design

1. Edit the geometry in `assets/brand/backspace.svg` (1024 viewBox).
2. Mirror the change in the `Write-CloudCheck` function inside
   `tools/build-icon.ps1` — the PS script is the source-of-truth
   renderer; the SVG is the human-readable spec. Both must agree.
3. If the sidebar logo visually matches the icon, update its
   primitives in `src/Backspace/Views/MainWindow.axaml` too (the
   shape coordinates there are scaled from the 1024 master to a 36×36
   tile — multiply each master value by `36 / 1024` ≈ `0.0352`).
4. Run `.\tools\build-icon.ps1 -All`.
5. Rebuild the app.
6. Commit the new PNGs and ICOs.

## Picking a different concept from the previews

`tools/build-concept-previews.ps1` generates ~36 candidate icons under
`assets/brand/concepts/` plus a contact-sheet PNG
(`assets/brand/concepts/_contact-sheet.png`). To swap to another
design entirely:

1. Open the contact sheet and pick a slug.
2. Copy the matching scriptblock from `build-concept-previews.ps1`
   into `build-icon.ps1`, replacing the body of `Write-CloudCheck`
   (and rename the function appropriately).
3. Update `backspace.svg` to match.
4. Update the sidebar logo's inline geometry in `MainWindow.axaml`
   if you want the brand wordmark to stay visually consistent with
   the taskbar icon.
5. Run `.\tools\build-icon.ps1 -All` and commit.

The preview script stays in the repo as a record of what was
considered, so future redesigns can pick up where this one left off.

## Future work

The current flow is script-based. A nicer v2 would integrate into
MSBuild — a `<Target>` that runs the PS script when the SVG is newer
than the ICO. For now, manual is fine and the outputs are small and
versioned.
