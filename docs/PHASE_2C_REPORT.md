# Phase 2C Report — Performance Truth + Production Hardening

Bench version 2C.1. Machine: Windows 10.0.19045 x64, 32 cores,
64 GB RAM. WebView2 152.0.4191.66, WinAppSDK 1.7.250606001.
Method: fresh Encomm process per scenario
(`scripts/run-performance-suite.ps1`), isolated profile per run
(`BenchmarkProfiles/<run-id>`: isolated SQLite + WebView2 user-data,
real profile untouched — verified 0 tabs in real profile after
runs), local BenchmarkSite over per-run unique loopback ports with
content verification, 8 s stabilization, every sample validated
(`ScenarioValid`: Live⇒view+Live, Warm⇒view+Warm, Ghost⇒no view).
All rows below are `ScenarioValid=yes`.

## Warm truth

Phase 2B's "Warm reclaims as much as Ghost" was INVALID (old settle
ghosted non-Live views; Views was 1). Phase 2C holds
`live ∪ warm` renderers through settling; scenario D fails unless
all 9 Warm views report `ViewLifecycleState.Warm` with 10 views
present. Measured (3 isolated runs):

| State | Logical | Views | Renderer RAM | Tree RAM (med/min/max) |
|---|---|---:|---:|---|
| 10 Live (D-pre) | 10 | 10 | 499 MB | 914 / 913 / 916 MB |
| 1 Live + 9 Warm (D) | 10 | 10 | 518 MB | 937 / 936 / 938 MB |
| 10 Live (E-pre) | 10 | 10 | 503 MB | 920 / 914 / 925 MB |
| 1 Live + 9 Ghost (E) | 10 | 1 | 51 MB | 455 / 454 / 458 MB |

**A suspended Warm WebView retains ~100% of its renderer RAM**
(937 vs 914 MB — noise). Suspend freezes CPU, not memory; only
process exit (Ghost) returns bytes (920 → 455 MB, −50%).

Policy consequence: Warm is a CPU/battery + instant-resume tool,
NOT a memory tool. Lifecycle strategy: short Warm grace for fast
switching, then Ghost for memory. Measurements drive policy.

## Ghost truth

Destroying 9 renderers returns ~465 MB to the OS (measured
before/after process-tree samples, 10→1 views, 16→7 processes).
Ghost ⇒ no-renderer is enforced in code (failed restores tear down
the partial view) and in measurement (validation gate).

## 100-tab result (fresh processes)

| Variant | Logical | Live | Views | Host WS | Tree RAM |
|---|---|---|---|---|---|
| H1 | 100 | 1 | 1 | ~180 MB | 437 MB |
| H3 (=H) | 100 | 3 | 3 | ~190 MB | 546 / 543 / 548 MB |
| H5 | 100 | 5 | 5 | ~195 MB | 666 MB |
| F (25/3) | 25 | 3 | 3 | 174 MB | 529 MB |
| G (50/3) | 50 | 3 | 3 | 181 MB | 535 MB |

Tab-metadata cost (CREATE, fresh process): 100 logical tabs in
**52.9 ms**, host working-set **+19 MB**, 0 views, tree 153 MB.

## Restore under load

10 samples per case, local pages (renderer-ready / navigation /
scroll / total):

- Case A (1 Live + 9 Ghost): n=9, total med **89 ms** (84–105).
- Case B (3 Live + 97 Ghost): n=10, total med **93 ms** (86–112).
- E-scenario restores: n=10, total med **88 ms**.
- Full-suite legacy median: 84–95 ms across runs.

**100 logical tabs do not materially hurt Ghost restore** (+4 ms).
Per-phase medians: renderer-ready ~35 ms, navigation ~47 ms,
scroll ~2 ms.

## Deployment

Unpackaged WinUI 3, self-contained .NET (`SelfContained=true`),
framework-dependent WinAppSDK 1.7 (`WindowsAppSDKSelfContained=false`,
explicit bootstrap in `Program.Main`) — pinned by unit test, fully
described in `docs/DEPLOYMENT.md` with the end-user installer plan
(check WinAppRuntime → install redist → check WebView2 → install
Encomm → launch; users never touch registration).

## Verification (new in 2C)

- Warm→Live resume, live: **9/9 resumed Live, 9/9 usable titles** in
  all 3 D runs (was unit-only before).
- Scroll, automatic: **set=2000 restored=2000 VERIFIED** (long page,
  record→restore→position end-to-end).
- Shield, local: tracker **blocked=1**, page title intact VERIFIED.
- Real-web sanity (WEB): example.com/org + iana.org render with
  correct titles, tree 560 MB for 3 live pages.
- Two real scrolling bugs fixed along the way: `GetScrollAsync`
  always returned (0,0) (object-vs-string result shape) and
  `PageContextParser.Parse` dropped scroll the same way; restores
  now settle scroll against layout (bounded, best-effort).
- Benchmark infrastructure bugs fixed: unique port per run
  (orphaned servers from killed runs used to spray connections),
  server content verification, UI-thread bench flow.
- Safe defaults confirmed, no change: permissions default-deny;
  downloads without UI land in the default location (never silently
  elsewhere).

## Tests

**90/90 passing** (was 73). 17 new in `BenchValidationTests`:
scenario-arg parsing (8), state-validation incl. "Warm without
renderer = physically Ghost" (6), restore-under-100-tabs (1),
scroll-record propagation (1), deployment csproj consistency (1).

## Known issues (no hiding)

- Live-page scroll can shift under capture (layout/anchor
  dynamics); restore honors the persisted record exactly, and ghost
  capture stays best-effort by design.
- Pre-ghost scroll for never-scrolled pages is trivially (0,0).
- Orphaned `python http.server` processes can linger after killed
  bench runs (bench kills its own on clean exit); harmless now that
  ports are unique per run, but untidy — manual cleanup if many
  accumulate.
- Permission/download dialogs remain live-unverified (safe
  fallbacks documented above).
- Thresholds in `scripts/compare-benchmark.ps1` are informational
  (+15% tree, +20% restore, renderer-count mismatch); CI runs
  build + unit tests only (no GUI/WebView in CI).

## Benchmark artifacts

- `artifacts/bench-<id>-<ts>.json`: isolated single-scenario
  results with versioning (commit, build, OS, cores, RAM, WebView2,
  WinAppSDK, benchmark version, timestamps). Git-ignored,
  regenerable.
- `artifacts/performance-suite.json/.md`: aggregated medians.
- `benchmarks/baseline-win-x64.json`: regression baseline.
- Full-suite compat output `artifacts/benchmark-results.{json,md}`
  still written by `--run-bench`.

STOP GATE: no AI Canvas, agents, semantic memory, MCP, LLM, or UI
redesign started. Phase 3 is a separate decision.
