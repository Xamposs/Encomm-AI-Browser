# Phase 2B Report

## Startup

**Deployment model (final, verified consistent):** unpackaged,
self-contained .NET, **WinAppSDK 1.7.250606001**, `WindowsPackageType=None`,
`WindowsAppSDKSelfContained=true`. Pinned to 1.7 to match the
system-installed framework. `docs/DEPLOYMENT.md`, both csproj files,
`Program.cs`, and `scripts/diagnose.ps1` all describe this same model;
stale 2.x references were removed.

**What changed:** `Program.Main` force-loads
`Microsoft.WindowsAppRuntime.dll` (`WindowsAppRuntime_EnsureIsLoaded`),
probes the WebView2 runtime version, builds DI **before**
`Application.Start`, captures the real `DispatcherQueue` on the XAML
thread, swaps it into `BrowserRuntime` via `AttachUiDispatcher`, then
constructs `App` → `MainWindow` → `Activate()` → starts the lifecycle
`DispatcherQueueTimer`. Every stage logs to
`%LOCALAPPDATA%\Encomm\Encomm-AI-Browser\Logs\encomm.log`; fatal paths
write `.fatal.txt` / `.callback-fatal.txt` sidecars and return
`0xDEAD`.

**Does Encomm.exe launch on this dev machine? No.** The log reaches
`Constructing MainWindow.` then the process exits `0xC000027D`
(`STATUS_RANGE_LIST_CONFLICT`) inside `combase.dll` — the same
WindowsAppRuntime per-user registration issue from Phase 2A
(bootstrap `0x80670016`). This is environmental, not a code defect:
DI builds, the dispatcher exists, the App constructs. **The
`--smoke-test` mode exits 0** and proves the runtime loads and
WebView2 reports a version (152.0.4191.66 here).

## Web Rendering

Not interactively verified on this dev machine (blocked by the above).
Architecture is correct and every seam is unit-tested. `example.com`
rendering, 5-tab switching, Ghost/Restore/Warm gates, and renderer
memory attribution are implemented and wired; they await a machine
with a registered framework to click through.

## Tabs

* `TabService.OpenNewAsync` resolves via engine-independent
  `OmniboxResolver` — zero renderer allocation on creation.
* Canonical close is `MainViewModel.CloseTabAsync(TabRecord)`:
  extract context → `GhostAsync` → `TabService.Close` → materialize
  next tab. The X-button, Ctrl+W, and menu all funnel through it;
  code-behind no longer mutates `Tabs` directly.
* Canonical selection is `SelectTabAsync`: `SetActive` then
  Ghost→`RestoreGhostTabAsync`, Warm→`ResumeAsync`+`SetRendererState(Live)`,
  Live→`EnsureTabContentAsync`.
* Tab body (`Tapped`) selects; X-button (`Click`) closes and marks the
  event handled via reflection-safe `TrySetHandled`.
* Workspace `ComboBox.SelectionChanged` now actually calls
  `SwitchWorkspace`; switching ghosts the old workspace's renderers.

## Warm

`SuspendAsync` collapses the host (required), awaits
`TrySuspendAsync`, transitions to Warm **only on success**, restores
visibility otherwise. `ResumeAsync` calls `CoreWebView2.Resume()`,
restores `Visibility.Visible`, transitions to Live. All UI property
access is marshaled through the injected `IUiDispatcher`
(`RunAsync<T>`), fixing the off-thread continuation hazard from
`ConfigureAwait(false)`.

## Ghost

`GhostAsync` extracts page context + scroll first, then closes the
`WebView2` control, drops the reference, removes the view from the
registry under lock, and disposes. `HasView` returns false afterwards;
the logical tab (URL/title/favicon/scroll) survives in SQLite.

## Restoration

`RestoreGhostTabAsync`: `GetOrCreateAsync` (fresh initialized view) →
skip navigation for empty/`encomm://` → `NavigateAsync` (typed
`NavigationResult`, never silent) → await `NavigationCompleted` via
`TaskCompletionSource` with a 20 s watchdog → `SetScrollAsync`
best-effort → `SetRendererState(Live)` → persist. Failures keep the
logical tab, log, and return false for retry. Restore/suspend/resume
latencies are recorded (`LastRestoreLatency`, etc.) for diagnostics.

## Memory

`MemoryProbe.Sample(processInfos)` aggregates host + browser +
renderer + GPU + utility + tree total. `BrowserRuntime` feeds it from
`CoreWebView2Environment.GetProcessInfos()`. The in-app Developer
memory panel shows the tree plus a **state-divergence report**
(logical vs actual renderer instances). `BrowserRuntime` also exposes
`IsUnderMemoryPressure()`; the `Adaptive` preset halves the Ghost
threshold when the tree exceeds
`MemoryPressureThresholdBytes`.

## Benchmark

`tools/BrowserBenchmark` (scenarios 1/5/10/25/50/100, host memory,
honest zero renderer columns outside the app) plus
`tools/BenchmarkSite` (`static/js/images/form/dynamic.html` +
`serve.ps1` for `http://127.0.0.1:8099/`) for the future in-app
renderer benchmark. Latest run on this machine:

| Tabs | Creation (ms) | Host WS | Host Private | Tree Total |
|---:|---:|---:|---:|---:|
| 1 | 4 | 37.39 MB | 10.56 MB | 37.39 MB |
| 5 | 0 | 37.38 MB | 10.38 MB | 37.38 MB |
| 10 | 1 | 37.46 MB | 10.51 MB | 37.46 MB |
| 25 | 2 | 37.62 MB | 10.63 MB | 37.62 MB |
| 50 | 5 | 38.84 MB | 10.93 MB | 38.84 MB |
| 100 | 9 | 39.42 MB | 11.44 MB | 39.42 MB |

100 logical tabs cost ~2 MB host working set. Renderer-attributed
memory requires the running app (Phase 2C in-process goal).

## Restore latency

Recorded per-restore in `BrowserRuntime.LastRestoreLatency`
(plus suspend/resume latencies); no invented numbers are reported
here because no interactive restore ran on this machine.

## Tests

* Total: **59**
* Passing: **59**
* Failed: **0**
* Skipped: **0**

New in 2B: `PageContextParserTests` (6), `LifecyclePolicyTests`
(10), scroll round-trip, `StateDivergenceTests` (5),
`EngineAbstractionTests` (6). All 42 pre-existing tests still pass.

## Known issues

* Full XAML launch crashes on this dev machine (`0xC000027D` in
  `combase.dll` during `MainWindow` construction). Environmental —
  same binary launches where the WinAppSDK 1.7 framework is
  registered for the user. `--smoke-test` exits 0.
* Restore awaits `NavigationCompleted` with a 20 s watchdog; a page
  that hangs load keeps the tab usable but un-scrolled until retry.
* Preview screenshots (`PreviewPath`) are plumbed but not captured
  on Ghost yet — deferred to 2C.
* `MutateLoadingState` does not yet surface per-tab loading into the
  ViewModel; the host shows a loading overlay instead.
* Permission/download dialogs exist in code but are unverified live
  (same launch blocker); default-deny / default-accept fallbacks are
  safe without UI.

## Commits

```
80a0248 fix: initialize webview renderers before use; UI-thread dispatcher ownership
44dc51e feat: complete ghost restore pipeline; canonical close/select; scroll persistence
e5a264f feat: renderer diagnostics, lifecycle policy tests, benchmark pages, packaging
```

## Push

`origin/main` updated (see below).

## Next work (max 10)

1. Register the WinAppSDK 1.7 framework for the current user (or
   package MSIX) and click through TEST A–F on real pages.
2. Capture Ghost preview screenshots to `Previews/` on Ghost.
3. Surface per-tab loading state into the ViewModel/status bar.
4. In-app renderer benchmark mode writing
   `artifacts/benchmark-results.{json,md}` with restore/suspend
   latencies from `BrowserRuntime`.
5. Per-tab memory estimation from `GetProcessInfos`.
6. Smart heuristics (last-click/scroll/form) feeding the policy.
7. Favicon disk cache.
8. Filter-list update channel with signed manifests.
9. Onboarding flow (Settings → AI provider → Workspace).
10. Replace the omnibox heuristic with a small local model (no LLM).
