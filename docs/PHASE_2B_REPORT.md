# Phase 2B Report (HEAD-verified)

Deployment: unpackaged, self-contained .NET, **WinAppSDK 1.7.250606001**,
`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`.
Machine: Windows 10.0.19045 x64/32. WebView2 runtime **152.0.4191.66**.

## Startup — launches

`Encomm.exe` launches on this dev machine. The earlier `0xC000027D` /
bootstrap `0x80670016` failure was environmental (missing per-user
framework registration) and is resolved by installing the Main /
Singleton / DDLM closure (`TryInitialize 0x00010007` returns
`hr=0x00000000`). `Program.Main` force-loads
`Microsoft.WindowsAppRuntime.dll`, builds DI before
`Application.Start`, captures the real `DispatcherQueue`, constructs
`App` → `MainWindow` → `Activate()`, starts the lifecycle
`DispatcherQueueTimer`. Every stage logs to
`%LOCALAPPDATA%\Encomm\Encomm-AI-Browser\Logs\encomm.log`.

Two smoke levels (exit 0/non-zero, no security weakening):

- `--runtime-smoke-test` (alias `--smoke-test`): headless, no XAML.
  WebView2 version discovery + data-folder write + SQLite round-trip.
  **PASS** on this machine.
- `--browser-smoke-test`: REAL browser test inside the XAML loop.
  Initializes dispatcher + `BrowserRuntime`, creates a real renderer,
  navigates a deterministic local page, awaits `NavigationCompleted`,
  verifies the title, disposes. **PASS**: `title=EncommSmoke-7F3A9`,
  renderer ghosted afterwards.

## Web rendering — live verified

`https://example.com` renders with title `Example Domain`
(`renderer_state=0`, restore ~1.3 s in the interactive session).
`example.net` → `Example Domain`, `iana.org` →
`Internet Assigned Numbers Authority`, all confirmed via persisted
titles after real navigations.

## Tabs + keyboard — live verified with real keystrokes

WebView2 child HWNDs bypass the XAML accelerator table, so owned
combos (Ctrl+L/T/W/R/Tab, Alt+Left/Right, F12) are forwarded from
page content via a capture-phase JS bridge + `WebMessageReceived` →
`BrowserRuntime.AcceleratorHandler` → commands on the UI thread
(`AcceleratorKeyPressed` lives on `CoreWebView2Controller` and is
unreachable from the WinUI control, so the bridge is the mechanism).

Synthetic-input verification on the live app: Ctrl+T with focus in
page content grew tabs 5 → 6; repeated Ctrl+Tab walked Ghost tabs
and restored them with correct titles. Canonical close
(`MainViewModel.CloseTabAsync`) and canonical select
(`SelectTabAsync`: Ghost→restore, Warm→resume+Live,
Live→ensure) are the only paths; X-button/keyboard/menu funnel
through them.

## Warm — real suspension, measured

`SuspendAsync` captures page context + scroll BEFORE calling
`TrySuspendAsync` (script execution after suspension is unreliable;
scroll failure never blocks suspension), collapses the host (required
by `TrySuspendAsync`), and marks Warm only on success.
`ResumeAsync` calls `CoreWebView2.Resume()`, writes
`Visibility.Visible`, READS IT BACK, and sets Live only when the
host actually reports Visible.

All adapter primitives (`SuspendAsync`, `ResumeAsync`,
`HasUnsavedFormStateAsync`, `CurrentUrl`/`CurrentTitle`) are safe
from any thread: every XAML-control touch (including the
`.CoreWebView2` null-guard getter, which is itself UI-affine and was
the source of production `RPC_E_WRONG_THREAD` suspend failures) is
marshaled through `IUiDispatcher`; off-thread property reads use
UI-thread-maintained caches. This fixed Warm in production, where
lifecycle-timer continuations run on pool threads.

Measured (scenario D, 10 local pages): 10 Live = 976 MB process
tree → 1 Live / 9 Warm = **517 MB** (renderer bytes 536 → 95 MB,
processes 16 → 7). Warm reclaims essentially as much as Ghost.

## Ghost — real renderer removal, measured

`GhostAsync` extracts context + scroll first, then closes the
`WebView2` control, drops the reference, removes the view under
lock, disposes. `HasView` is false afterwards; URL/title/scroll
survive in SQLite. Measured (scenario E): 10 Live = 985 MB →
1 Live / 9 Ghost = **523 MB** (renderer bytes 533 → 93 MB,
processes 16 → 7, views 10 → 1).

## Restoration — strict success semantics

`RestoreGhostTabAsync` returns true (and marks Live) ONLY when
navigation actually reaches a usable document. Timeout /
cancellation / navigation failure / rejected navigation tears down
the partial view (so Ghost ⇒ no-renderer holds), leaves the logical
tab in Ghost with URL intact (retryable), records `LastRestoreError`,
and returns false. Scroll restoration runs only on success.
Per-phase cost is exposed as `RestoreBreakdown`
(renderer-ready = create+init, navigation, scroll, total).
The navigation waiter subscribes before navigating and ignores the
expected `about:blank` `ConnectionAborted` on fresh views.

Measured restore cost, local pages, 5 real Ghost restores:

| Phase | Median | Min | Max |
|---|---:|---:|---:|
| Renderer ready (create+init) | 35 ms | 34 ms | 48 ms |
| Navigation | 47 ms | 27 ms | 48 ms |
| Scroll | 2 ms | 2 ms | 4 ms |
| Total Ghost-to-usable | 84 ms | 77 ms | 87 ms |

## Memory — real WebView2 process-tree benchmark

In-app runner (`Encomm.exe --run-bench`, `src/Encomm.Browser.App/Services/BenchRunner.cs`):
local BenchmarkSite pages over `http://127.0.0.1:8099/`, 8 s
stabilization per phase, `MemoryProbe` over host +
`CoreWebView2Environment.GetProcessInfos()`. Results written to
`artifacts/benchmark-results.{json,md}`. Latest full run:

| Scenario | Host WS | Host Priv | WV2 Browser | WV2 Renderer | WV2 GPU | WV2 Util | Tree Total | Procs | Tabs L/W/G | Views |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|---|---|
| A: 1 logical / 1 Live | 146.16 MB | 86.76 MB | 111.88 MB | 95.16 MB | 59.34 MB | 52.02 MB | 464.55 MB | 7 | 1/0/0 | 1 |
| B: 5 logical / 5 Live | 152.02 MB | 90.91 MB | 131.96 MB | 293.13 MB | 62.29 MB | 52.54 MB | 691.93 MB | 11 | 5/0/0 | 5 |
| C: 10 logical / 10 Live | 162.45 MB | 100.08 MB | 155.66 MB | 538.59 MB | 65.24 MB | 53.17 MB | 975.11 MB | 16 | 10/0/0 | 10 |
| D-pre: 10 logical / 10 Live | 164.92 MB | 102.31 MB | 156.05 MB | 536.16 MB | 65.52 MB | 53.28 MB | 975.94 MB | 16 | 10/0/0 | 10 |
| D: 10 logical / 1 Live / 9 Warm | 164.31 MB | 102.46 MB | 138.48 MB | 94.73 MB | 64.27 MB | 54.99 MB | 516.77 MB | 7 | 1/9/0 | 1 |
| E-pre: 10 logical / 10 Live | 169.79 MB | 107.94 MB | 161.47 MB | 532.10 MB | 66.34 MB | 55.46 MB | 985.17 MB | 16 | 10/0/0 | 10 |
| E: 10 logical / 1 Live / 9 Ghost | 170.20 MB | 108.43 MB | 139.84 MB | 93.41 MB | 64.68 MB | 55.13 MB | 523.26 MB | 7 | 1/0/9 | 1 |
| F: 25 logical / 3 Live / 22 Ghost | 173.71 MB | 112.25 MB | 137.95 MB | 195.67 MB | 64.40 MB | 55.10 MB | 626.82 MB | 9 | 3/0/22 | 3 |
| G: 50 logical / 3 Live / 47 Ghost | 180.97 MB | 120.77 MB | 133.32 MB | 194.91 MB | 64.70 MB | 55.08 MB | 628.98 MB | 9 | 3/0/47 | 3 |
| H: 100 logical / 3 Live / 97 Ghost | 189.63 MB | 129.77 MB | 135.11 MB | 238.98 MB | 65.00 MB | 55.13 MB | 683.85 MB | 10 | 3/0/97 | 3 |

The key result: **100 logical tabs run with 3 renderers**
(683.85 MB tree, 189.63 MB host). No RAM reclamation is inferred —
every delta above is a measured before/after process-tree sample.

## Tests — 73/73

- Pre-existing: 59 pass (parser, policy, persistence, shield, …).
- New `RuntimeSemanticsTests` (9): restore success/failure/watchdog/
  rejection/cancellation semantics, suspend capture order,
  scroll-failure tolerance, ghost teardown, breakdown sanity.
  A fake engine behind `BrowserRuntime.CreateEngineForTests`
  (production never sets it) plus inline dispatcher; no WebView2.
- New `LifecycleInvariantTests` (5): A Ghost⇒no renderer, B
  Warm⇒view exists and is Warm, C Live⇒view exists and is usable, D
  active tab never left Ghost after restore, E closed tab has no
  renderer and no persisted record.

## Verification matrix

| Capability | Unit tested | Integration tested | Live verified |
|---|---|---|---|
| Startup | | | x (launch + both smoke levels) |
| WebView initialization | | | x (browser smoke + 100+ real renderers in bench) |
| Navigation | x (resolver) | | x (real + local pages) |
| Tab create | | | x (Ctrl+T, bench OpenNew) |
| Tab switch | | | x (Ctrl+Tab incl. Ghost restore) |
| Tab close | | | x (bench Ghost+Close over 100+ tabs; X-button funnels to same path) |
| Warm suspend | x (order + tolerance) | | x (scenario D, real TrySuspend, -459 MB) |
| Warm resume | | | ( Resume path unit-covered via fakes; live Warm→Live click-through not yet run ) |
| Ghost | x (teardown) | | x (scenario E, -462 MB, views 10→1) |
| Ghost restore | x (strict semantics) | | x (Ctrl+Tab + 5 timed restores, median 84 ms) |
| Scroll restore | x (round-trip) | | partial (SetScroll executes ~2 ms; position not visually confirmed) |
| Shield | x (block rules) | | (not live-verified against tracking pages) |
| Download | | | (dialog + default-accept fallback exist; not live-verified) |
| Permissions | | | (dialog + default-deny fallback exist; not live-verified) |
| Memory accounting | x (probe) | | x (full process-tree bench) |
| 100 logical tabs | | | x (scenario H: 100 logical / 3 Live) |

Nothing is marked Live Verified on unit tests alone.

## Known issues (honest, HEAD-accurate)

- After mass tab cleanup, a stale host-control refresh can attempt a
  restore of an already-removed tab; it now fails strictly
  (`LastRestoreError` = watchdog timeout, tab stays Ghost,
  retryable) instead of falsely reporting success. Benign.
- `PreviewPath` plumbed but screenshots not captured on Ghost yet.
- Per-tab loading state stays in the host overlay, not the ViewModel.
- Permission/download dialogs exist but are unverified live;
  fallbacks (default-deny / default-accept) are safe without UI.
- `TabService.SetActive` marks the tab Live optimistically; the
  select path immediately restores/resumes/ensures, and the host
  shows Error with reselect-retry if that fails.
- Renderer bytes in F/G/H include shared browser/GPU processes; the
  tree total is the authoritative number.
- No UI redesign, AI Canvas, agents, or LLM work was started in this
  phase (per scope gate).
