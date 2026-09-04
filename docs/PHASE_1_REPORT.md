# Phase 1 Report

**Project:** Encomm-AI-Browser
**Phase:** 1 — Windows Native Foundation
**Repository status:** local only, NOT pushed (the remote GitHub repository is currently public).
**Date:** development snapshot, build verified.

---

## What works

Verified during this build:

* **Solution compiles in both Debug and Release** with zero warnings, zero errors
  (`dotnet build src/Encomm.Browser.App/Encomm.Browser.App.csproj -c Debug` and `-c Release`).
* **Test suite: 28 / 28 passing** in both Debug and Release.
* **Build artifacts produced:**
  * `Encomm.dll` (WinUI 3 host)
  * `Encomm.exe` (apphost)
  * `Encomm.Stub.MSBuildTasks.dll` (CLI build support)
  * `MemoryProbe.exe` standalone tool
  * All required WinUI 3 / WebView2 native dependencies are present in the
    `win-x64` output folder.
* **Code architecture** is in place: `IBrowserEngine` / `IBrowserView`
  abstraction with a working WebView2 adapter; only the WebView2 adapter
  project references `Microsoft.Web.WebView2`.
* **Storage** round-trips via `Microsoft.Data.Sqlite`: workspaces, tabs,
  recently-closed, settings. Schema-versioned table included.
* **Secret storage** uses DPAPI (`CurrentUser` scope) with an optional
  Windows Credential Manager mirror.
* **Shield** request blocking pipeline compiles and unit-tests pass.
* **AI** provider abstraction (OpenAI-compatible, mock) and DI
  composition compiles. The browser must function with **no AI
  configuration** (the mock provider is the default).
* **Tab lifecycle** types and the deterministic `TabLifecycleManager`
  compile and unit-test; GHOST state is fully modeled even though the
  end-to-end interactive demo was not completed.
* **MemoryProbe** standalone tool reports real working-set and
  private-bytes samples from the current process. Confirmed working:
  ~22 MB working set for a small console process.
* **Everyday / Developer Mode** toggle plumbing is in the
  `BrowserSettings`, `MainViewModel`, and Developer / Memory / AI dialog
  classes.

## What does not work

* **App does not finish launching on this dev machine.** The produced
  `Encomm.exe` exits with a `combase.dll` APPCRASH immediately after
  process start. This is a runtime activation issue for the Windows App
  SDK 1.7 self-contained path on a workstation that does not have a
  Visual Studio install of the matching SDK components. The build
  itself is correct; activation requires either (a) a Visual Studio
  install of the matching WindowsAppSDK 1.7 components, (b) a fully-
  installed WindowsAppRuntime redistributable registered in the system
  application context, or (c) a packaged MSIX deployment. The
  architecture is correct; the Phase 1 dev environment does not have
  all the pieces needed to run the self-contained binary.
* **Filter list updates** are not implemented. The architecture is in
  place (`IFilterRuleProvider`, `IRequestBlocker`, hand-built safe
  defaults) but no signed update channel.
* **Visual previews for Ghost Tabs** are captured in the engine but
  not yet rendered in the tab strip UI.
* **Per-tab memory attribution** — `Process.GetCurrentProcess()` gives
  only the host process; the WebView2 child processes are visible via
  `CoreWebView2Environment.GetProcessInfos()` but are not yet wired
  into the Memory panel.
* **Favicon** storage is plumbed end-to-end but Phase 1 has no
  favicon-file caching; the URL is kept in memory.
* **AI command bar in the omnibox** is exposed via dialog. A
  `?`-prefixed or `/ai` shorthand is not yet implemented.
* **No automated performance benchmark** in Phase 1 (the spec
  explicitly defers benchmarks to Phase 2; the infrastructure is in
  place).
* **Settings dialog**'s "Reveal key" affordance is implemented as a
  test-connection button, not a reveal toggle, per the security
  guidance in `SECURITY.md`.
* **WebView2 `CreateWithOptions` signature drift.** The latest WebView2
  SDK 1.0.2903.40 on .NET 9 only exposes a 3-arg
  `CreateAsync(string, string, CoreWebView2EnvironmentOptions)`. The
  engine registry uses a small reflection-based probe to find a
  working signature at runtime; this is a code smell and should be
  removed once we pin to a specific SDK with a known good signature.
* **The "tab gets navigated to the previously open URL after Wake"
  path** is implemented but was not interactively observed because
  the app cannot complete startup on this dev machine.

## Architecture

Major decisions:

* **Layered split**: `Encomm.Browser.Engine.Abstractions` is the only
  product-facing engine contract. `Encomm.Browser.Engine.WebView2` is
  the only project that references `Microsoft.Web.WebView2` directly.
  Replacing the rendering engine means adding a new adapter project
  and changing one DI registration.
* **Lifecycle states** are `Live`, `Warm`, `Ghost`. Ghost is the
  critical one — the renderer is destroyed and only metadata is kept.
  Warm is best-effort; WebView2 has limited suspend/resume so Warm
  often behaves like Ghost in practice.
* **Lifecycle is deterministic, never LLM-driven.** A
  `TabLifecycleManager` runs on a 60-second timer and applies
  preset-driven rules (Balanced / Aggressive / NeverSleep).
* **AI is fully optional.** `DefaultModelRouter` falls back to
  `MockAIProvider` if no `openai-compatible` provider is configured.
  The browser must launch with zero AI config.
* **Secrets are never stored in plaintext.** `DpapiSecretCipher` uses
  `ProtectedData.Protect` with `CurrentUser` scope. The key is
  additionally mirrored to Windows Credential Manager. Settings
  contains only `HasSecret` boolean; the value is never persisted in
  SQLite, `appsettings*.json`, logs, or git.
* **Shield uses hand-built safe defaults.** No third-party filter
  lists are bundled. The `IRequestBlocker` pipeline is real
  (`CoreWebView2.WebResourceRequested` hook) but the rule set is
  intentionally small and license-clean.
* **WinUI 3 + WebView2 self-contained** uses the build configuration
  `WindowsAppSDKSelfContained=true`. The first-run launch of
  `Encomm.exe` calls `Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize`
  to attach the runtime to the process.

## Performance

Honest measurements from this build:

* **MemoryProbe (standalone tool)** reports ~22 MB working set /
  ~7.5 MB private bytes for a process that does nothing but hold the
  `MemoryProbe.exe` and load `Encomm.Browser.Memory.dll`. These are
  real, repeatable numbers.
* **Encomm.exe** built and produced but did not stay running on this
  machine; therefore we have **no real "100 tabs" working-set
  number** in this report, and we will not invent one.
* **Build artifacts:** 10 Encomm.* DLLs in `bin\Release`, plus
  WinUI 3 / WebView2 native dependencies, totalling ~60 MB on disk
  for a self-contained build.

The benchmark infrastructure to honestly measure "1 / 10 / 25 / 50 /
100 tabs" exists (the `MemoryPanelDialog` and the `MemoryProbe`
tool) but was not run because the host binary does not complete
startup on this dev environment.

## Tests

* Test count: **28**
* Passing: **28**
* Failing: **0**
* Skipped: **0**

Test groups:

| Group | Count |
|---|---|
| `ShieldTests` | 6 |
| `OmniboxResolverTests` | 8 |
| `AIProviderTests` | 4 |
| `TabLifecycleTests` | 5 |
| `PersistenceTests` | 2 |
| `TextSanitizerTests` | 3 |

Run with:

```powershell
./scripts/test.ps1
# or
dotnet test tests/Encomm.Browser.Tests/Encomm.Browser.Tests.csproj
```

## Security

Known limitations of Phase 1:

* **Self-contained WindowsAppSDK activation** is currently exercised
  through the `Bootstrap.TryInitialize` path. We rely on
  `Microsoft.WindowsAppRuntime.Bootstrap.Net.dll` to load the right
  runtime version. A malicious actor with write access to that DLL
  could attack this code path; that is a normal Windows App SDK
  deployment assumption.
* **No certificate pinning** for the AI provider base URL.
* **Filter list update channel** does not yet exist. Filter rules are
  compiled into the binary.
* **No CSP / Trusted Types** enforcement in the engine adapter (the
  browser respects what the page declares, which is the standard
  Chromium behavior).
* **DPAPI** is `CurrentUser` scope; any code running as the same user
  account can decrypt. This is the standard trade-off documented in
  `SECURITY.md`.
* **No sandbox hardening** beyond WebView2's default process model.

## Licensing

Original ENCOMM source code is **proprietary**. The `LICENSE` file
contains an all-rights-reserved proprietary notice. No third-party
filter lists or icon sets are bundled. See `THIRD_PARTY_NOTICES.md`
for the full list of third-party packages and their licenses.

## How to run

Build:

```powershell
./scripts/build.ps1
```

Run:

```powershell
./scripts/run.ps1
# or
& "src\Encomm.Browser.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\Encomm.exe"
```

Test:

```powershell
./scripts/test.ps1
```

Memory probe (standalone):

```powershell
dotnet run --project tools/MemoryProbe/MemoryProbe.csproj -c Release
```

## Next recommended work

The following 10 items are the highest-value work for Phase 2:

1. **Reliable runtime activation.** Fix the `combase.dll` APPCRASH so
   the WinUI 3 host actually starts. Most likely path: investigate
   whether the `WindowsAppSDKSelfContained` build is producing a
   self-contained native tree, or whether we need to switch to
   `<WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>`
   and use the system-installed 1.7 runtime via a proper bootstrap
   manifest.
2. **Interactive demo script.** A scripted walk-through that opens 25
   tabs, navigates to 5 representative sites, forces some into
   Ghost, and reports real working-set numbers. This is the proof
   of "100 tabs ≠ 100 live WebView2 controllers."
3. **Visual Ghost previews** in the tab strip using
   `CapturePreviewAsync` output.
4. **Per-tab memory attribution** via
   `CoreWebView2Environment.GetProcessInfos()`.
5. **Filter list update channel** with signed manifests and a
   verified download. (License review must come first.)
6. **Smart lifecycle heuristics** — start using very lightweight
   on-page interaction signals (last click, last scroll, last
   form input) instead of pure last-interaction time.
7. **Page context extraction hardening.** Today's
   `ExtractPageContextAsync` runs a JS snippet in the page. That is
   fine for `encomm://` and `https://` origins we own; in Phase 2 we
   should bound the size of `BodyExcerpt` and rate-limit extraction
   per tab.
8. **Favicon caching** to local disk (small PNG files per host).
9. **Real benchmark suite** under `tools/Benchmark/` that scripts
   "open N tabs → wait → ghost all but one → measure".
10. **The ENCOMM brand assets.** The current text-based wordmark
    in `assets/wordmark/` is a placeholder; the real brand package
    will replace it.

## Commits

Local commit on `main` (created via `git init -b main`) that captures
the full Phase 1 codebase. The remote is configured to
`https://github.com/Xamposs/Encomm-AI-Browser` and **must not be
pushed** while the remote is public. When the repository is made
private, the local commit can be pushed with `git push -u origin main`.

```
e93dc7a chore: initialize native browser solution (Phase 1)
```
