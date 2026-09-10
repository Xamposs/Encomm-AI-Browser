# ENCOMM UI Design System (implementation reference)

Dark is the flagship. Light is a clean off-white sibling with the
same accents. No neon fills; gradients are rationed.

## Palette

| Token | Dark / Default | Light |
|---|---|---|
| Background | #080A10 | #F2F4F8 |
| Surface | #0D1018 | #FFFFFF |
| Elevated | #121722 | #FFFFFF |
| Secondary | #171D2A | #E8ECF2 |
| Border | #252C3B | #D4DBE5 |
| Text primary | #F4F7FB | #141A24 |
| Text secondary | #9AA6B7 | #4A5568 |
| Text muted | #657084 | #8A94A6 |
| Tab inactive | #0B0E15 | #E8ECF2 |
| Omnibox | #10141D | #EDF1F6 |

Accents (theme-independent): Cyan #4DE1FC, Blue #66A4F9,
Purple #A62DFB. Danger #E5484D, Success #3FB27F, Warning #E8A13C.

Defined in `Themes/EncommColors.xaml` (`Default`=dark values,
`Dark` mirrors, `Light` variant, `HighContrast` fallback).

## Semantic brushes (`Themes/EncommBrushes.xaml`)

`EncommBackgroundBrush`, `EncommSurfaceBrush`,
`EncommElevatedBrush`, `EncommSecondaryBrush`,
`EncommBorderBrush`, `EncommTextPrimary/Secondary/MutedBrush`,
`EncommTabInactiveBrush`, `EncommOmniboxBrush`,
`EncommAccentCyan/PurpleBrush`, `EncommDanger/Success/WarningBrush`.

## Gradient usage rules (rationed)

Allowed: `EncommGradientBrush` (cyan→blue→purple) for the
workspace badge, AI glyph accent, logo ambience;
`EncommActiveIndicatorBrush` for selection/focus accents;
`EncommFocusRingBrush` for the omnibox focus border;
`EncommAmbientBrush` (radial, near-black) behind the New Tab logo.
Forbidden: full-surface gradient fills, per-tab gradient
backgrounds, glowing neon inputs, animated backgrounds.

## Typography (`Themes/EncommTypography.xaml`)

Native Segoe UI Variable only. `EncommChromeText` 12px secondary,
`EncommChromeTitle` 13px semibold primary, `EncommTabTitle` 12px
primary with ellipsis, `EncommQuietText` 11px muted,
`EncommNewTabHeading` 28px semibold display, `EncommNewTabSub`
14px, `EncommDialogHeading` 14px semibold.

## Spacing / radii

Chrome padding 8; gaps 4–6 (toolbar), 8–12 (panels); toolbar
buttons 32×32 radius 6; omnibox height 36 radius 10 border 1;
tabs min-height 34 radius 6 margin right 4; tab title max 180;
flyouts 280–320 wide; dialogs per-component.

## Controls (`Themes/EncommControls.xaml`)

`EncommToolbarButton` (32px icon-only, transparent),
`EncommAiButton` (cyan glyph), `EncommMarkFallback` (gradient E),
`EncommTabItemContainer` (ListViewItem: inactive surface, default
selection visuals mark the active tab), `EncommWorkspaceButton`,
`EncommOmnibox` (transparent TextBox inside a bordered pill; focus
ring swapped from code), `EncommNewTabAction` (220×40 pill button).

## Tab states

- Active: ListView selection (elevated surface + primary title).
- Inactive: `EncommTabInactiveBrush`, secondary text.
- Pinned/muted: pin (E718) / mute (E767) glyphs, muted color.
- Ghost, Everyday: title + initial + ☾ whisper (no "renderer" word,
  never looks broken; click restores normally).
- Ghost/Warm/Live, Developer: explicit LIVE/WARM/GHOST badge.
- Widths: content-sized, title max 180, horizontal scroll strip.
- Context menu: Pin/Unpin, Mute/Unmute, Duplicate, Sleep, Ghost,
  Close, Close Others. Tab search flyout filters metadata only.

## Omnibox states

Rest: 1px `EncommBorderBrush` on `EncommOmniboxBrush` pill.
Focus: 1.5px `EncommFocusRingBrush` (code-swapped, never neon).
Placeholder: "Search or enter address". Left: site identity
(lock + flyout). Right inside pill: Shield, ENCOMM AI (✦).
Address shows "" for `encomm://` surfaces.

## Workspace states

DropDownButton: gradient initial badge + name + native chevron.
Flyout: current (semibold) + list, New/Rename/Delete with dialogs.
Delete guarded (never the last workspace).

## Everyday vs Developer rules

Everyday hides: lifecycle jargon, renderer counts, provider
details, memory internals, dev strip, state badges. Shows: browse,
tabs, workspaces, Shield, ENCOMM AI, settings.
Developer adds: LIVE/WARM/GHOST badges, slim dev strip (status,
mode, workspace, DevTools, Memory), memory panel, DevTools (F12).
Same chrome, enhanced layer — not a different app.

## Everyday error language

No exception type names, no "renderer" word: "Couldn't open this
page. Reselect the tab to retry.", "Renderer not ready. The engine
may still be starting.", "Couldn't display this page. Details are
in the log." Loading/restoring use the compact bottom toast.

## Glyphs (Segoe MDL2 Assets; E113 renders as ★ on Win10 — avoid)

Back E112, Forward E76C (chevron-right), Reload E117, New tab
E109, Close tab E711, Search tabs/menu chevron E70D, Menu E712,
Lock E72E, Shield E83D, Pin E718, Mute E767, AI/text sparkle U+2726,
Ghost whisper U+263E. Verified by rendering the font on-target.

## Toolchain constraints (WinAppSDK 1.7 XAML compiler)

Learned the hard way; do not regress:

1. New `local:` types declared in markup crash the compiler
   silently (MSB3073, no message) — declare converters in a
   prebuilt referenced assembly (`Encomm.Browser.UI`) or set all
   visuals from code.
2. x:Bind + Converter inside a Window root fails codegen
   (`SetConverterLookupRoot` needs FrameworkElement) — use
   classic `{Binding}` with code-registered converters, or code.
3. `Window` is not a `DependencyObject`/`FrameworkElement` — no
   DPs, no `Resources` bag, cannot be an ElementName binding
   source. Shared state lives in `App.DevMode` (`DevModeState`).
4. `Application.Resources` is unreachable during App construction
   (0x8000FFFF) — register instance resources in MainWindow ctor,
   never in App ctor.
5. `ListViewItemPresenter` properties (SelectedBackground, …) do
   not exist on `ListViewItem` — same silent crash.
6. Missing `xmlns:` prefixes for `x:DataType` crash the same way.
7. ContentDialogs render in a separate popup root: they ignore the
   window content's `RequestedTheme` — theme every dialog via
   `App.ApplyDialogTheme`.
