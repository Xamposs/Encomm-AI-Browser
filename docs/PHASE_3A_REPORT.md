# Phase 3A Report — Premium Product UI + ENCOMM Identity

## What visually changed

- Design system: `Themes/EncommColors|Brushes|Typography|Controls.xaml`
  merged via `App.xaml`. All chrome binds semantic resources; no
  hard-coded hex in `MainWindow.xaml`.
- Dark-first flagship (default `Theme=Dark`), full Light palette,
  System support. Theme setting actually applies at runtime, no
  restart; every ContentDialog is themed explicitly (popup roots
  ignore window `RequestedTheme`).
- Integrated title bar (`ExtendsContentIntoTitleBar`, native caption
  buttons/drag/snap preserved) with tab strip, brand slot, + and
  tab-search buttons.
- Icon-only toolbar (Back E112, Forward E76C chevron, Reload E117),
  workspace DropDownButton (gradient initials + name, native
  chevron), rounded omnibox pill (identity lock + flyout, Shield,
  cyan ✦ ENCOMM AI, menu). Text Back/Forward/Reload/Stop buttons,
  Go button, status bar, and generic ComboBox chrome are gone —
  functionality kept (Stop command remains API-accessible).
- Tab strip: `ListView` selection IS the active tab; initial badge
  (no favicon fetch, no renderer), pin/mute glyphs, close ×,
  adaptive widths (title max 180), horizontal scroll, right-click
  menu (Pin/Unpin, Mute/Unmute, Duplicate, Sleep, Ghost, Close,
  Close Others), tab-search flyout (metadata filter, 100 cap).
- Ghost language: Everyday shows title + ☾ whisper (never the word
  "renderer", never broken-looking); Developer shows LIVE/WARM/
  GHOST badges. Selecting a Ghost tab feels normal (restore path).
- Workspace switcher: current + list + New/Rename/Delete dialogs.
- Native New Tab surface (no WebView): ENCOMM lockup (official PNG
  slot with calm text fallback), search box, Search + Ask ENCOMM,
  Recent Workspaces. Opening 10 blank tabs allocates 0 renderers
  (verified: close path skips `GetOrCreateAsync` when `!HasView`;
  restore/ensure/host all fast-path `encomm://` before allocating).
- ENCOMM AI entry (✦, gradient accent) opens the existing command
  surface, renamed "ENCOMM AI" (mock-provider note, no provider
  IDs/tokens in Everyday).
- Shield entry (E83D) with flyout: status, session blocked count,
  protection toggle wired to settings + live blocker.
- Dev strip replaces the permanent status bar (status/mode/
  workspace/DevTools/Memory, Developer only). Loading/errors use a
  compact bottom toast with Everyday-safe wording (no exception
  type names, no "renderer" word).
- Settings redesigned into General/Appearance/Privacy & Shield/
  Performance/ENCOMM AI/Developer NavigationView (credentials only
  under ENCOMM AI; theme applies on save).

## Theme implementation

`BrowserSettings.Theme` → `App.ApplyTheme()` →
root `RequestedTheme` (Light/Dark/Default). Default changed
System→Dark. Dialogs via `App.ApplyDialogTheme` (popup-root
behavior documented in `docs/UI_DESIGN_SYSTEM.md`).

## New Tab renderer cost

Zero by construction + test: `EnsureTabContentAsync(encomm://)` and
restores fast-path before allocation; `CloseTabAsync` skips
allocation when `!HasView`; host never parents native tabs.
Unit-covered (`EnsureTabContent_encomm_url_creates_no_view`,
`CloseTab_never_materialized_creates_no_renderer`).

## Tab / Workspace / Shield / AI UX

Verified live with screenshots: dark tabs + Ghost whispers,
toolbar, omnibox, workspace flyout (current + New/Rename/Delete),
Shield flyout (status/count/toggle), AI dialog, Settings (dark,
all categories bound), dev mode (badges + strip), light theme,
1000×700 narrow window. `docs/screenshots/` holds 8 captures.

## Accessibility

Every icon button has AutomationProperties name + tooltip (proven
by UIA enumeration: Back/Forward/Reload/Workspace/Site
information/Shield/AI/Menu/tab closes/caption buttons all
exposed). Keyboard fully preserved: Ctrl+L/T/W/Shift+T/Tab/
Shift+Tab/R, Alt+Left/Right, F12 (spot-verified Ctrl+T/Tab live;
all handlers untouched in code).

## Performance before/after

`scripts/compare-benchmark.ps1` on the UI build: **no regressions
flagged**. Scenario E: tree 474 MB (baseline 455, +4%), host ~175
MB (~+5 MB chrome cost), restores median 90 ms (baseline 88),
scroll+shield verifications still pass. Ghost architecture
untouched; tab UI is metadata-only (`ContainerContentChanging`
paints from records; search caps at 100 rows).

## Tests

**108/108** (was 90). New: `ViewModelSemanticsTests` (close/select
renderer discipline), `UiPresentationTests` (theme mapping, badge/
hint/accessible-name rules, workspace initials),
`StateDivergenceTests.No_error_when_native_live_tab_has_no_renderer`
(native Live needs no WebView — inspector exemption added).

## Live verification

Dark + Light + System-path themes; new tab; real webpage;
5+ tabs; workspace flyout; Ghost restore + Warm resume via new UI
(menu Sleep → Warm in DB → Ctrl+Tab → Live, titles intact);
Shield/AI/Settings/dev-mode/memory-panel via UIA; keyboard
shortcuts; narrow window; benchmark regression.

## Known issues (no hiding)

- E113 renders as ★ in this Win10 MDL2 build (font truth table in
  design doc); Forward uses E76C chevron instead. If other glyphs
  misbehave on other builds, swap per the table.
- WinAppSDK 1.7 XAML compiler cannot instantiate new `local:`
  types in markup (silent MSB3073), x:Bind+Converter fails codegen
  on Window roots, `ListViewItemPresenter` props don't exist on
  `ListViewItem` — all documented with workarounds used.
- Persisted logical-Live tabs with no renderer after restart are
  (correctly) flagged by the divergence inspector until selected;
  native Live tabs are exempt.
- One transient native crash observed during heavy UIA automation
  (modal dialog + synthetic input reentrancy); app-level
  `UnhandledException` sidecar added; not reproduced in normal use.
- 100-tab strip scrolled visually with 5–9 tabs; strip
  virtualizes via ItemsStackPanel, renderers never materialize for
  UI (bench H proves the runtime side).
- Official `encomm-logo.png` / `encomm-mark.png` not yet supplied:
  gradient-E / ENCOMM-text fallbacks active; placement documented
  in `Assets/Brand/README.md`. **Owner action: drop in the two PNGs
  (no code change needed).**

## Latest commit

See git log (Phase 3A sequence below).
