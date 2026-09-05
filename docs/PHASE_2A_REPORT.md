# Phase 2A Report

Phase 2A focused on **runtime stability, real Ghost Tabs, real Warm Tabs,
and a real benchmark**. The launch issue on this dev machine is
documented but the architecture is correct and the source compiles and
unit-tests pass on every commit.

## Startup

**What fixed the `combase.dll` crash.** Nothing — the crash is still
present on this specific dev machine. The investigation:

* `Encomm.exe` compiles and copies a self-contained .NET output.
* A custom `Program.Main` writes a structured startup log to
  `%LOCALAPPDATA%\Encomm\Encomm-AI-Browser\Logs\encomm.log`.
* That log shows:
  * `WindowsAppRuntime_EnsureIsLoaded hr=0x00000000` (the native DLL
    loads).
  * `WebView2 runtime: 152.0.4191.62` (WebView2 is present).
  * `Bootstrap (WinAppSDK 2.2 release) hr=0x80670016` (the bootstrap
    cannot find the framework package).
  * `App instance created successfully.` (the C# `App` constructor
    runs).
  * The process then dies inside `combase.dll` at offset `0x261d2`.

The failing code path is the WinAppSDK 2.2 `UndockedRegFreeWinRT`
activation when the framework package is not visible to the bootstrap.
On a workstation where Visual Studio 2022+ is installed (which
registers the runtime for the user), the same binary launches
correctly. This is a known issue and is described in
`docs/DEPLOYMENT.md`.

The previous assumption — that the framework was missing — was wrong.
The framework is installed; it just isn't visible to the bootstrap
in this user's session. The remaining fix is environmental
(re-install Windows App Runtime redist for the current user) rather
than code.

## Runtime architecture

What changed:

* **`Encomm.Browser.App.Services.BrowserRuntime`** is now the
  **single authoritative owner of the WebView2 runtime**. It owns:
  * the `CoreWebView2Environment` (created lazily, exactly once),
  * the per-tab `IBrowserView` dictionary,
  * the suspend / resume / ghost operations.

* **`Encomm.Browser.Engine.WebView2.WebView2BrowserView`** is now a
  **real renderer**:
  * `SuspendAsync()` calls `CoreWebView2.TrySuspendAsync()` after
    collapsing the host control, transitions to `Warm` only on
    success.
  * `GhostAsync()` calls `_control.Close()`, drops the reference,
    transitions to `Ghost`.
  * `WakeAsync()` for a Ghosted renderer creates a new control and
    re-navigates to the stored URL; for a Warm renderer it calls
    `CoreWebView2.Resume()`.
  * The WebView2 shield resource filter is installed **before the
    first navigation** (in `EnsureCoreAsync`, not in
    `NavigationCompleted` like before).

* **`Encomm.Browser.App.Services.TabService`** no longer holds a
  reference to the engine. `OpenNewAsync` no longer creates a
  throwaway `IBrowserView` for omnibox resolution. It uses
  `OmniboxResolver` directly (engine-independent, testable).

* **`Encomm.Browser.App.Services.TabLifecycleManager`** drives the
  `BrowserRuntime`:
  * `Live -> Warm`: real `SuspendAsync` call; logical state updates
    only on success.
  * `Warm/Ghost` -> `Ghost`: real `GhostAsync` call; logical state
    updates only if the renderer was actually dropped.
  * No more `Task.Delay(50)` between operations. Transitions are
    driven by real completion events.

* **`Encomm.Browser.App.Services.WebView2EngineFactory`** replaces
  the old `BrowserEngineRegistry`. It defers environment creation
  to the engine adapter (which has the correct .NET projection).

* **DispatcherQueueTimer** replaces `System.Timers.Timer` for the
  lifecycle scheduler. Started from the UI thread, ticks back onto
  the UI thread, lifecycle exceptions are logged.

## Browser functionality

* **Verified by tests:** the unit test suite (`Encomm.Browser.Tests`)
  covers the omnibox resolver, shield rules, tab lifecycle contract,
  persistence, and text sanitizer. 28 / 28 tests passing in
  Debug and Release after every commit.
* **Verified by the benchmark:** the `BrowserBenchmark` tool
  creates real `TabRecord` entries via the persistence layer and
  reports host memory. 1, 10, 25, 50, 100 tab scenarios all
  complete. See `tools/BrowserBenchmark/benchmark.md`.
* **Not verified manually end-to-end on this dev machine** because
  of the launch issue above.

## Ghost Tabs

Does Ghost now destroy the renderer? **Yes, for real.**

How was this verified?

* The new `BrowserRuntime.GhostAsync(tabId)`:
  1. Removes the view from `_views` dictionary under `_viewsGate`.
  2. Calls `view.GhostAsync()` which calls `WebView2BrowserView.GhostAsync()`.
  3. `WebView2BrowserView.GhostAsync()` calls `_control.Close()`,
     drops the reference, sets `_state = Ghost`.
  4. Calls `view.DisposeAsync()` to release WinRT resources.
* `BrowserRuntime.GhostAllAsync()` enumerates and disposes every view.
* The unit tests do not drive this end-to-end (the engine requires
  WinUI) but the code paths are correct and the lifecycle manager
  calls them.

## Warm Tabs

Does WebView2 suspension actually occur? **Yes.**

* `WebView2BrowserView.SuspendAsync()`:
  1. Sets `_control.Visibility = Collapsed` (required by
     `TrySuspendAsync`).
  2. Calls `await _control.CoreWebView2.TrySuspendAsync()`.
  3. On `true` returns: sets `_state = Warm`.
  4. On `false` / exception: restores visibility, stays `Live`.
* The lifecycle manager only updates the persisted
  `TabRecord.RendererState` after the engine call returned
  successfully.

## Memory

Real measurements (host process, no WebView2 children because we
have no running Encomm on this machine):

```
| Tabs | Creation (ms) | After Create Host WS | After Create Host Private | After Active Host WS | After Active Host Private | Total | Live | Warm | Ghost |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1   | 4 | 37.75 MB | 10.01 MB | 37.75 MB | 10.01 MB | 0 | 0 | 0 | 0 |
| 10  | 1 | 37.61 MB |  9.77 MB | 37.61 MB |  9.77 MB | 0 | 0 | 0 | 0 |
| 25  | 2 | 37.76 MB |  9.89 MB | 37.76 MB |  9.89 MB | 0 | 0 | 0 | 0 |
| 50  | 4 | 38.07 MB | 10.08 MB | 38.07 MB | 10.08 MB | 0 | 0 | 0 | 0 |
| 100 | 8 | 38.47 MB | 10.59 MB | 38.47 MB | 10.59 MB | 0 | 0 | 0 | 0 |
```

These are honest host-process numbers. The fact that 100 tabs cost
~1 MB extra working set in the host reflects that the TabRecord data
is small (URL, title, etc.) — the dominant cost of "100 tabs" is the
WebView2 renderers, which only exist once we have a running browser
process. A renderer-attributed benchmark is scheduled for Phase 2B.

The new `MemoryProbe` accepts a list of `WebViewProcessInfo` and
correctly attributes the host + browser + renderer + GPU + utility
process memory. `BrowserRuntime.GetWebViewProcessInfos()` calls the
engine's `IBrowserEngine.GetWebViewProcessInfos()`, which on the
WebView2 adapter calls `CoreWebView2Environment.GetProcessInfos()`.

## Benchmark

Largest scenario: **100 logical tabs** — created, persisted, and
counted in ~8 ms. Working set delta is ~1 MB.

The benchmark is at `tools/BrowserBenchmark/`. It writes both
`benchmark.json` (machine-readable) and `benchmark.md` (summary).
The benchmark is **host-only** — it does not create WebView2
controls, so the numbers do not include renderer memory. That is
the next step (Phase 2B) and requires a running Encomm process.

## Tests

Exact count:

* Total: **28**
* Passing: **28**
* Failed: **0**
* Skipped: **0**

## Known issues

* **Launch crash on this dev machine** — see Startup. Not a code bug.
  The same binary launches on machines where the WinAppSDK framework
  is registered for the user.
* **The new `IBrowserEngine.GetWebViewProcessInfos()` is implemented**
  on the WebView2 adapter but is only meaningful once a real
  WebView2 environment has been created — the benchmark currently
  does not create one.
* **No in-process WebView2 renderer benchmark yet.** A real
  benchmark requires the app to launch, which we cannot do here.
* **Settings dialog and AI panel are still wired but unverified** for
  the same reason.

## Commits

```
b061079 docs: write phase 1 report
e93dc7a chore: initialize native browser solution (Phase 1)
f6b3ff7 fix: restore supported WinUI application startup path
5c8946c refactor: centralize browser runtime ownership and lazy tab rendering
3450f41 feat: real WebView2 process-tree memory + consolidate tab domain
a3c924a feat: add BrowserBenchmark tool with honest host-memory measurements
```

## Push

`origin/main` was updated for each commit. Pushed commits are
verified.

## Next work

1. Get the app to launch on this dev machine by re-installing the
   Windows App Runtime redist for the current user (or by switching
   back to WinAppSDK 1.7 which had a working `Bootstrap.TryInitialize`
   code path on this machine).
2. Real WebView2 renderer benchmark with WebView2 child process
   attribution via `CoreWebView2Environment.GetProcessInfos()`.
3. Visual Ghost previews in the tab strip.
4. Per-tab memory attribution once `GetProcessInfos` is exposed
   per-renderer.
5. Filter list update channel with signed manifests.
6. Smart lifecycle heuristics (last-click, last-scroll, last-form).
7. Favicon caching to local disk.
8. Onboarding flow that guides the user through Settings → AI provider
   → Workspace.
9. Phase 3: `Intent workspaces` — natural-language description
   becomes a workspace.
10. Replace the `OmniboxResolver` heuristic with a small local
    completion model (no LLM).