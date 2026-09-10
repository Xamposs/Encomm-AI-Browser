# ENCOMM Brand Assets

Authoritative artwork is supplied by the owner (see the official
ENCOMM logo: near-black base, cyan → blue → purple luminous
gradient). **Do not redraw, recolor, or reinterpret the logo.**

## Placement

| File | Used by | Notes |
|---|---|---|
| `encomm-logo.png` | New Tab page (full lockup) | Transparent or near-black background, ≥ 480px wide |
| `encomm-mark.png` | Toolbar brand button (symbol only) | Square, transparent, ≥ 96px |
| `encomm-logo-mono.png` | High-contrast / fallback surfaces | White or black single-color |

## Behavior when missing

The UI never hard-depends on these files:

- Toolbar: `Image` loads `ms-appx:///Assets/Brand/encomm-mark.png`;
  on `ImageFailed` the slot collapses to a restrained gradient "E"
  glyph fallback (see `MainWindow.xaml` brand button).
- New Tab: `Image` loads `encomm-logo.png`; on failure a styled
  "ENCOMM" text lockup shows instead.

## Adding the files

1. Export from the authoritative logo (do not recreate).
2. Drop the PNGs into this folder with exactly the names above.
3. They are picked up as `Content` (`PreserveNewest`, see the App
   csproj) — no code change needed. Dark-first: prefer transparent
   backgrounds so both themes work.
