# Performance Strategy

Performance is a first-class product feature for Encomm-AI-Browser.
"100 tabs" must not mean "100 live WebView2 controllers".

## Memory hierarchy

```
Live  : full WebView2 controller + GPU surface + process
Warm  : suspended where the engine supports it
Ghost : renderer destroyed; only metadata kept
```

* A **Live** tab is the only state that has an active renderer.
* A **Warm** tab is best-effort suspended; WebView2 suspend/resume is
  limited so a Warm tab may be functionally close to a Ghost in many
  cases.
* A **Ghost** tab is the critical one: the WebView2 control is
  disposed, the underlying Chromium process frees its resources, and
  the tab keeps only metadata (URL, title, favicon reference, last
  interaction time, preview if available, workspace, pinned state).

## Tab lifecycle rules (Phase 1)

The `TabLifecycleManager` runs every minute and applies these rules:

* Tabs that are **active**, **pinned**, **muted**, **Keep Awake**, or
  interacted with within `WarmAfter` minutes are protected.
* Background tabs that have been idle for `WarmAfter` are demoted to
  `Warm`.
* Background tabs that have been idle for `GhostAfter` are demoted to
  `Ghost` — the renderer is destroyed.

Presets (configurable in Settings):

| Preset | WarmAfter | GhostAfter |
|---|---|---|
| Balanced | 5 min | 20 min |
| Aggressive | 2 min | 8 min |
| NeverSleep | 60 min | 240 min |

The user can also force `Sleep Now` and `Ghost Now` from the tab
context menu.

## How to measure

### In-app

Open the **Memory & Lifecycle** panel (Developer Mode menu) to see:

* Browser process working set and private bytes
* Live / Warm / Ghost tab counts
* Shield blocked-request counts
* Per-tab renderer state

### Standalone

Run the included `MemoryProbe` tool to print the working set of a
test process:

```powershell
dotnet run --project tools/MemoryProbe/MemoryProbe.csproj -c Release
```

### Benchmark plan (manual, Phase 1+)

The Phase 1 build ships the infrastructure to measure, not an
automated benchmark. A future benchmark will:

1. Spawn N tabs (1, 10, 25, 50, 100) to a fixed list of representative
   sites (search engine, news, docs, dev portal, social).
2. Wait 60 s for the lifecycle manager to demote inactive tabs.
3. Read `Process.WorkingSet64` and `Process.PrivateMemorySize64` for
   the browser process.
4. Read `Process.WorkingSet64` of the WebView2 child processes via
   `CoreWebView2Environment.GetProcessInfos()`.
5. Force-everything-ghost and re-measure.

We do **not** publish benchmark numbers in Phase 1. The architecture
to support honest measurement is in place; running the actual
benchmark is Phase 2 work.

## Performance rules that govern the codebase

* No Electron.
* No embedded frontend runtime beyond what WinUI 3 itself uses.
* AI is **never** in the lifecycle / hot path. The lifecycle manager
  is pure deterministic code.
* LLM calls are off by default; when made, they run in a separate
  background service and cannot block the UI thread.
* Caches are bounded. Secret values, page contexts, and previews are
  size-limited.
* Logging is bounded and rotates at 5 MB.
* No polling when events are available.

## Things explicitly deferred

* Per-tab memory attribution (a real memory probe would need
  per-renderer-process working set; WebView2's `GetProcessInfos` is
  available in the SDK but Phase 1 only reads the host process).
* Visual previews for Ghost Tabs in the tab strip (capture works in
  the engine; the UI rendering is Phase 2).
* Smart prediction of which tab the user is likely to revisit next
  (would inform lifecycle choices).