# Deployment

This document explains the current deployment model of the Encomm-AI-Browser
binary, why we chose it, and the known limitations. Read this first if
launching the produced `Encomm.exe` fails.

## Current decision: **unpackaged, self-contained .NET, WinAppSDK 1.7**

```
<OutputType>WinExe</OutputType>
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
<TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.7.250606001" />
```

* `WindowsPackageType=None` — unpackaged (no MSIX).
* `WindowsAppSDKSelfContained=true` — the WinAppRuntime payload is
  extracted into the build output (`MsixContent/`) so the app carries
  the exact runtime it was built against.
* `SelfContained=true` — .NET runtime is shipped in the bin output so
  no .NET install is required to run the EXE.
* Pinned to **Windows App SDK 1.7.250606001** to match the
  system-installed `MicrosoftCorporationII.WinAppRuntime.1.7`
  framework on the dev machine. Do not upgrade the SDK without
  re-verifying startup, build, and tests in one focused commit.

The Windows App Runtime native DLL (`Microsoft.WindowsAppRuntime.dll`)
is force-loaded from the application directory at startup
(`Program.Main` → `WindowsAppRuntime_EnsureIsLoaded`), which is the
same call the SDK's UndockedRegFreeWinRT auto-initializer makes.

### Rationale

* **Memory efficiency** — the .NET self-contained build already adds
  ~60 MB of framework assemblies. Shipping the full WinAppRuntime MSIX
  inside that would more than double the on-disk size, AND the
  WinAppRuntime MSIX is shared across processes on the machine
  (browser, electron, other WinAppSDK apps), so a system install
  actually reduces TOTAL memory across apps.
* **Simple public distribution** — the produced bin output is a
  regular xcopy-deployable folder. No MSIX signing, no Store, no
  Windows App Cert Kit.
* **Clean servicing** — Windows Update handles the WinAppRuntime; the
  user does not need to update the browser binary every time Microsoft
  pushes a runtime patch.
* **Reproducible builds** — framework-dependent gives byte-identical
  output across machines, which is important for an internal alpha.

### What must be present on the target machine

* Windows 10 19041 or later (we test on 10.0.19045).
* WebView2 Runtime ≥ 110 (we test on 152.x).
* No Visual Studio required for end users. The self-contained output
  carries .NET and the WinAppRuntime payload; only the WebView2
  Runtime is a shared system prerequisite (it ships with Windows 11
  and current Edge).

If the WebView2 Runtime is missing, `Program.Main` logs
`WebView2 runtime probe FAILED` and `--smoke-test` exits non-zero
with an actionable message. `scripts/diagnose.ps1` reports the
relevant environment.

### Why self-contained?

On this project's dev machine the framework-package bootstrap
(`MDDbootstrap TryInitialize`) returns `0x80670016`
(`MDD_E_PACKAGE_NOT_FOUND`) even though a WinAppRuntime framework is
installed — the package is not visible to the bootstrap in this user
session, and the process then dies inside `combase.dll` when XAML
tries to activate. Self-contained carries the exact runtime payload
in the output so behavior does not depend on per-user framework
registration. The trade-off is on-disk size (~60 MB of framework
assemblies plus the runtime payload).

If a future WinAppSDK release unifies the projections, we will
re-evaluate.

### Why not an MSIX (packaged)?

Packaging adds:
* Code-signing (Authenticode) — required for the MSIX.
* A manifest that pins the application identity.
* Store / sideload deployment complexity.

None of this is in scope for Phase 2A.

## Verified

* `dotnet build src/Encomm.Browser.App/Encomm.Browser.App.csproj -c Release` —
  succeeds with zero warnings.
* `dotnet test tests/Encomm.Browser.Tests/Encomm.Browser.Tests.csproj` —
  28 / 28 tests passing in Debug and Release.
* `tools/BrowserBenchmark` runs end-to-end and produces a JSON +
  Markdown report at `tools/BrowserBenchmark/benchmark.{json,md}`.
* `scripts/diagnose.ps1` produces a safe environment report at
  `diagnose.txt`.

## Known launch issue on this dev machine

On the workstation where Phase 2A was developed, the produced
`Encomm.exe` does not finish launching. The cause is that the
installed Windows App Runtime 2.2 MSIX is not visible to the
bootstrap's package lookup (it returns `0x80670016`
`MDD_E_PACKAGE_NOT_FOUND`). The same binary will launch on any
machine where the framework package is properly registered for the
current user (e.g. any Visual Studio 2022+ install, or after
running `WindowsAppRuntimeInstall.exe` from the WinAppSDK redist).

See `docs/PHASE_2A_REPORT.md` for full details.
## Phase 2B addition

A --smoke-test mode verifies WinAppRuntime + WebView2 load without launching XAML. The Encomm architecture now drives real CoreWebView2.TrySuspendAsync (Warm), real Ghost (WebView2 control disposal), and real Ghost restore (fresh view + stored-URL navigation + scroll restoration). State-divergence diagnostics in the in-app Developer Mode memory panel detect logical/actual renderer mismatches. The benchmark tool is host-only; renderer-attributed memory is available inside the running app.
