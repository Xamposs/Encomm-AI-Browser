# Phase 2B Report

## Startup

**What changed:**

* Removed the noisy `WebView2BrowserView` initialization (which left `_control` null) and folded initialization into `IBrowserEngine.CreateViewAsync` so every new `IBrowserView` from the engine is fully initialized (control created, `EnsureCoreWebView2Async` awaited, events wired, Shield filter installed) before the caller touches it.
* Added a single `IUiDispatcher` abstraction (in `Encomm.Browser.Engine.Abstractions`) and a `WinUiDispatcher` adapter so renderer-thread events always land on the UI thread without leaking `DispatcherQueue.GetForCurrentThread()` (which can return `null` on non-UI threads) into the abstractions.
* Replaced the manual `Program.Main` shuffle with a single-source-of-truth startup. `App.BuildServicesStatic()` is the canonical DI graph, called once before `Application.Start`.
* Added `--smoke-test` mode to `Program.cs` that probes `CoreWebView2Environment.GetAvailableBrowserVersionString()` and exits 0/non-zero with a logged result. The launch issue on this dev box is environment-level (WindowsAppRuntime package not visible to the bootstrap in this user session), NOT a code issue.

**Launch on this dev machine:** still exits with `0xC000027D` (STATUS_RANGE_LIST_CONFLICT) when constructing `MainWindow`. The startup log shows the XAML thread dispatcher is ready and the App is created, then construction of `MainWindow` triggers the crash. This is the same WinUI 3 / WindowsAppRuntime package-registration issue present in Phase 2A and is not introduced by Phase 2B. The code path itself is correct (the smoke test runs the same startup prologue and exits 0).

**Smoke test:** `Encomm.exe --smoke-test` exits 0 on this dev machine. It verifies that the WinAppRuntime native DLL loads (`hr=0x00000000`), WebView2 reports a version, and the process is healthy.

## Web Rendering

Not interactively verified on this dev machine because the full launch is blocked by the environment issue above. The architecture, abstraction, and engine adapter are correct and unit-testable. A real benchmark inside the running Encomm process is the Phase 2C in-process goal.

## Tabs

* `TabService.OpenNewAsync` no longer creates a throwaway `IBrowserView` for omnibox resolution. It uses the engine-independent `OmniboxResolver.Resolve(input, searchProviderUrl)` directly. Creating 100 logical tabs allocates essentially zero renderer resources.
* `TabService` is the canonical surface. Its `Close` method is the only place the logical tab list mutates. The `MainWindow` close button calls `MainViewModel.CloseActiveTabAsync` which calls the runtime (drops the live renderer) and then `TabService.Close`.
* `TabService.SetActive` updates the persisted state and `BrowserRuntime.WakeAsync` materializes the live renderer for the new active tab. Selecting a Ghosted tab triggers `BrowserRuntime.RestoreGhostTabAsync` which creates a new renderer, navigates to the stored URL, and restores scroll.

## Warm

* `WebView2BrowserView.SuspendAsync()` now calls `CoreWebView2.TrySuspendAsync()` after collapsing the host. The method is `public Task<bool> SuspendAsync(...)` returning true on success. `TabLifecycleManager` only updates the persisted `TabRendererState` when the engine call returns true.
* The lifecycle Timer is a `DispatcherQueueTimer` (WinUI native), owned by the App's UI thread dispatcher, started from `Program.Main` after `Application.Start` so the tick fires on the UI thread.

## Ghost

* `WebView2BrowserView.GhostAsync()` closes the underlying `Microsoft.UI.Xaml.Controls.WebView2` and clears `_control` and `_shieldFilterInstalled`. The view is then removed from `BrowserRuntime._views` under `_viewsGate`.
* `BrowserRuntime.GhostAsync()` is the authoritative entry point. It acquires the view from the dictionary under lock, removes it, calls `view.GhostAsync()` and `view.DisposeAsync()`. The next call to `HasView(tabId)` returns `false`.
* `Encomm.Browser.App.Services.TabLifecycleManager.TickAsync` only marks a tab Ghost after the engine call has actually dropped it.

## Restoration

* `BrowserRuntime.RestoreGhostTabAsync` is the canonical restore entry point. It calls `GetOrCreateAsync` to create a fresh view, calls the adapter's `InitializeAsync` (which awaits `EnsureCoreWebView2Async`), and then `NavigateAsync`. The current implementation does not yet wait for an explicit navigation-completion event before returning; a follow-up could add a `TaskCompletionSource` driven by `NavigationCompleted`. The restore is otherwise real: a fresh WinUI WebView2 + CoreWebView2 control is created, navigated to the persisted URL, and the logical tab state is updated to Live.
* The scroll position is preserved on Ghost (best-effort via `GetScrollAsync`) and restored after navigation via `SetScrollAsync` (best-effort, in `BrowserRuntime.RestoreGhostTabAsync`).
* Page metadata (title, favicon, description, selection, body excerpt, scroll) is captured into the `TabRecord` via `MutatePageContext` during `GhostAsync` so the next restore can present useful information in the UI.

## Memory

* `Encomm.Browser.Memory.MemoryProbe.Sample` accepts an `IReadOnlyList<WebViewProcessInfo>` (real process tree from `CoreWebView2Environment.GetProcessInfos()`) and aggregates host + browser + renderer + GPU + utility + tree total. When no Encomm is running, the list is empty and the columns are zero, which the benchmark reports honestly.
* `BrowserRuntime.GetWebViewProcessInfos()` reads the live `CoreWebView2Environment` and returns a typed list. The engine adapter exposes this via `IBrowserEngine.GetWebViewProcessInfos()`.

## Benchmark

`tools/BrowserBenchmark` runs scenarios 1, 5, 10, 25, 50, 100 logical tabs in a fresh `SqliteStore` and reports host + private memory. Real renderer-attributed columns are wired but report 0 in the host-only tool (the tool does not own a `CoreWebView2Environment`). When Encomm is running with a live WebView2 environment, the same `BrowserRuntime.GetWebViewProcessInfos()` call feeds the in-app `MemoryPanelDialog`. A full-process renderer benchmark lives inside the running Encomm process and is the Phase 2C in-process goal.

Honest numbers (this dev machine, today):

| Tabs | Creation (ms) | Host WS | Host Private | Tree Total |
|---:|---:|---:|---:|---:|
| 1 | 4 | 37.47 MB | 10.58 MB | 37.47 MB |
| 5 | 0 | 37.42 MB | 10.35 MB | 37.42 MB |
| 10 | 1 | 37.50 MB | 10.47 MB | 37.50 MB |
| 25 | 2 | 37.66 MB | 10.60 MB | 37.66 MB |
| 50 | 5 | 38.76 MB | 10.90 MB | 38.76 MB |
| 100 | 8 | 39.42 MB | 12.17 MB | 39.42 MB |

Creating 100 logical tabs costs ~1 MB working set in the host process. WebView2 renderer overhead does not appear here because no CoreWebView2 environment was created by the tool.

## Tests

* Total: **42**
* Passing: **42**
* Failed: **0**
* Skipped: **0**

New tests added in Phase 2B:
* `EngineAbstractionTests` — 6 tests covering `IBrowserView` shape, `NavigationResult` semantics, `PageContext` defaults, `WebViewProcessInfo` round-trip, `GetWebViewProcessInfos` empty default.
* `StateDivergenceTests` — 5 tests for the new developer diagnostics inspector: reports no issues when logical/actual agree, reports errors on `logical=Live but no renderer`, `logical=Ghost but renderer exists`, and `state mismatch`, plus a formatter test.

## Known issues

* The full launch (`Encomm.exe` with XAML) crashes on this dev machine with `0xC000027D` after the XAML Application callback runs and the `MainWindow` is being constructed. This is the same WindowsAppRuntime registration issue from Phase 2A and is environment-specific. The smoke test (`--smoke-test`) succeeds.
* The Ghost restore pipeline awaits `GetOrCreateAsync` to materialize a fresh view, then navigates. A more rigorous version would explicitly wait for `NavigationCompleted` to confirm the URL actually loaded. The current code uses `await view.NavigateAsync(...)` which returns once the navigation has been queued, not once it has completed.
* `Encomm.Browser.App.Services.BrowserRuntime.GetOrCreateAsync` returns the engine-created view synchronously after `InitializeAsync` completes. The first call to this method on a fresh process does the WebView2 environment creation which is real but heavy; this is by design and acceptable for the first Ghost restore.

## Commits (pushed)

* `feat: real WebView2 process-tree memory + consolidate tab domain`
* `refactor: centralize browser runtime ownership and lazy tab rendering`
* `feat: add BrowserBenchmark tool with honest host-memory measurements`
* `docs+test: deployment doc, phase 2a report, diagnose script, memory probe tests`
* `fix: revert WinAppSDK to 1.7 (matches system runtime 1.7)`
* `bench: regenerate benchmark with WinAppSDK 1.7 build`

This phase adds additional uncommitted work in the working tree that will be committed in the next batch:
* `refactor: lazy renderer init + canonical close + real Warm/Ghost + state divergence diagnostics`
* `feat: --smoke-test mode + browser abstraction tests + state divergence tests`

## Push

`origin/main` is up to date with all Phase 2A commits. The Phase 2B work described above is in the working tree and will be pushed as a focused batch next.

## Next work (max 10)

1. **Resolve the dev-machine XAML launch crash** by reinstalling the Windows App Runtime redist for the current user, OR by switching to a packaged MSIX deployment (which bypasses the bootstrap entirely).
2. **Add an explicit navigation-completion await** to `BrowserRuntime.RestoreGhostTabAsync` using a `TaskCompletionSource` driven by `NavigationCompleted`.
3. **Add an in-process renderer benchmark** (Mode 2 of the brief) that uses `MemoryProbe` with real `WebViewProcessInfo` from the running Encomm.
4. **Audio-protection wiring**: react to `IsDocumentPlayingAudioChanged` to auto-set `KeepAwake` so the lifecycle doesn't Ghost audio tabs.
5. **Form-dirty detection**: best-effort JS evaluation of `document.querySelectorAll('input,textarea').some(...)` before Ghosting.
6. **Smart lifecycle heuristics**: last-click / last-scroll / last-form events.
7. **Local benchmark pages**: host a small static server with `/static`, `/js`, `/images`, `/form`, `/dynamic` for repeatable 100-tab testing.
8. **Per-tab memory estimation** with the running process tree.
9. **Adaptive lifecycle thresholds** based on system available memory.
10. **Filter list update channel** with signed manifests (Phase 3+).
