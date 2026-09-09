# Deployment

Live-verified configuration (matches `src/Encomm.Browser.App/Encomm.Browser.App.csproj` —
a unit test pins these three properties, so this document cannot drift silently).

## Current decision: unpackaged WinUI 3, self-contained .NET, framework-dependent WinAppSDK 1.7

```
<OutputType>WinExe</OutputType>
<WindowsPackageType>None</WindowsPackageType>
<WindowsAppSDKSelfContained>false</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
<WindowsAppSdkBootstrapInitialize>false</WindowsAppSdkBootstrapInitialize>
<TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.7.250606001" />
```

In precise terms:

- **Application**: unpackaged WinUI 3 desktop app (`WindowsPackageType=None`, no MSIX, no Store).
- **.NET**: self-contained (`SelfContained=true`). The output carries the .NET runtime; the user needs no separate .NET install.
- **Windows App SDK**: framework-dependent (`WindowsAppSDKSelfContained=false`).
  The WinAppRuntime payload is NOT bundled. Explicit bootstrap
  (`WindowsAppSdkBootstrapInitialize=false`, and `Program.Main`
  calls `Bootstrap.TryInitialize`) requires a compatible
  `MicrosoftCorporationII.WinAppRuntime.1.7` framework registered for
  the current user. `Program.Main` also force-loads
  `Microsoft.WindowsAppRuntime.dll` from the application directory
  (`WindowsAppRuntime_EnsureIsLoaded`).

The earlier `0xC000027D` / bootstrap `0x80670016` launch failure on
the dev machine was environmental (missing per-user framework
registration), resolved by installing the Main / Singleton / DDLM
framework closure. **The app launches**; both smoke levels pass.

### What must be present on the target machine

- Windows 10 19041+ (tested on 10.0.19045) or Windows 11.
- WinAppRuntime 1.7 framework registered for the user (comes with
  Visual Studio 2022+, or the WinAppSDK 1.7 redist installer).
- WebView2 Runtime 110+ (tested on 152.0.4191.66; ships with
  Windows 11 and current Edge). Missing runtime → startup log
  records `WebView2 runtime probe FAILED` and smoke tests exit
  non-zero.
- No Visual Studio required for end users.

`scripts/diagnose.ps1` reports the relevant environment.

### Rationale (why not bundle the runtime)

- The .NET self-contained output is already large; bundling the full
  WinAppRuntime payload would grow it further.
- The WinAppRuntime framework is shared across processes on the
  machine (other WinAppSDK apps, widgets), so a system install
  reduces TOTAL memory and servicing churn.
- Windows Update / the redist installer services the runtime; the
  browser binary does not rev on every Microsoft runtime patch.
- Output stays xcopy-deployable: no MSIX signing, no Store, no
  App Cert Kit.

If a future WinAppSDK release changes the trade-off, re-evaluate in
one focused commit with a launch + test re-verification. Do NOT
flip deployment properties to make documentation easier; document
reality (this file) and let installers ensure prerequisites.

## Diagnostics

- `Encomm.exe --runtime-smoke-test`: WebView2 discovery + data-folder
  write + SQLite round-trip, no XAML. Exit 0/non-zero.
- `Encomm.exe --browser-smoke-test`: real renderer + local-page
  navigation + title verification inside the XAML loop.
- `Encomm.exe --run-bench[=ID]`: in-app renderer benchmark into an
  isolated profile (`BenchmarkProfiles/<run-id>`), never touching
  the real user profile.

## Verified (Phase 2C)

- `dotnet build src/Encomm.Browser.App/Encomm.Browser.App.csproj -c Release` — zero warnings.
- `dotnet test tests/Encomm.Browser.Tests/Encomm.Browser.Tests.csproj -c Release` — 90/90.
- In-app benchmark scenarios A–H + H1/H3/H5 + CREATE/RESTORE/WEB with
  measured process-tree memory; see `docs/PHASE_2C_REPORT.md`.

## End-user runtime plan (future installer/bootstrapper)

Normal users must never install Visual Studio, run developer
commands, or troubleshoot framework registration. A future
installer/bootstrapper (not implemented in this phase) should:

1. Detect OS version (block below Win10 19041 with a clear message).
2. Check for a compatible WinAppRuntime 1.7+ framework registration.
3. Install the WinAppSDK 1.7 redist (`WindowsAppRuntimeInstall.exe`)
   if missing (per-user, no elevation beyond the redist's own needs).
4. Check the WebView2 Runtime version (Evergreen installer bootstrapper).
5. Install/refresh WebView2 Runtime if missing or too old.
6. Install Encomm (xcopy layout + Start Menu shortcut; MSIX only if
   identity/store distribution is chosen later).
7. Launch the browser and run `--runtime-smoke-test` silently first;
   on failure, show the log location instead of a blank window.

Until that installer exists, distribution = pinned Release folder +
this document.
