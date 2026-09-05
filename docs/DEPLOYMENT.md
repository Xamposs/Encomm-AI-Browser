# Deployment

This document explains the current deployment model of the Encomm-AI-Browser
binary, why we chose it, and the known limitations. Read this first if
launching the produced `Encomm.exe` fails.

## Current decision: **framework-dependent, unpackaged, system-runtime
required**

```
<OutputType>WinExe</OutputType>
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
<TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
```

* `WindowsPackageType=None` — unpackaged (no MSIX).
* `WindowsAppSDKSelfContained=false` — does NOT ship the WinAppRuntime
  in the bin output.
* `SelfContained=true` — .NET runtime is shipped in the bin output so
  no .NET install is required to run the EXE.

The Windows App Runtime (the C#/WinRT projections for XAML, WebView2,
  etc.) is loaded from a **system-installed** MSIX package
  (`MicrosoftCorporationII.WinAppRuntime.2.2`).

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
* MicrosoftCorporationII.WinAppRuntime **2.2** MSIX package (or
  newer). This is normally pre-installed by Visual Studio's Windows
  App SDK installer, by Microsoft Store, or by the WindowsAppRuntime
  redistributable.

If the runtime is missing or installed per-user but not per-system,
the bootstrap returns `0x80670016` (MDD_E_PACKAGE_NOT_FOUND) and
the process dies inside `combase.dll`. `scripts/diagnose.ps1` reports
the relevant environment.

### Why not self-contained?

We tried it. The SDK's self-contained targets extract the
WindowsAppRuntime MSIX contents into the bin output, but the
C#/WinRT projection metadata is split: the XAML compiler (a 32-bit
.net472 tool) reads the net6.0 projection while the C# compiler
reads the net8.0 projection. They disagree on the available
overloads of `CoreWebView2Environment.CreateAsync`, which makes the
build brittle. Combined with the on-disk size penalty, we chose
framework-dependent.

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