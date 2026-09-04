# Build Environment

This document describes the build environment used to compile
Encomm-AI-Browser Phase 1. It does **not** contain personal machine
information.

## Required toolchain

| Tool | Minimum version | Notes |
|---|---|---|
| Windows | 11 (10.0.19041 or later) | x64 |
| .NET SDK | 9.0.300+ | `dotnet --version` |
| WebView2 Runtime | Any recent stable | Pre-installed on Windows 11 |
| Windows App SDK | 1.7 runtime | Auto-installed by `WindowsAppSDKSelfContained=true` for unpackaged apps |
| Visual Studio | **Not required** | All build steps use the .NET SDK CLI |

The build does **not** require Visual Studio or the .NET Framework
targeting pack. It is fully self-contained via CLI.

## Why a stub MSBuild task assembly?

Windows App SDK 1.5+ buildTransitive targets reference MSBuild tasks
(`Microsoft.Build.AppxPackage.*`, `Microsoft.Build.Packaging.Pri.Tasks.*`)
that ship only with Visual Studio's AppxPackage component. When
building with the .NET SDK alone, those DLLs are absent and the build
fails with `MSB4062`.

Phase 1 is not an MSIX application. We do **not** need PRI generation
or Appx packaging. We therefore provide empty stub implementations of
those tasks in:

- `tools/StubTasks/Encomm.StubTasks.csproj`
- `tools/StubTasks/Encomm.Stub.MSBuildTasks.cs` (source) — see StubTasks/EncommStubTasks.cs
- `Directory.Build.props` (UsingTask overrides)
- `Directory.Build.targets` (auto-build of the stub assembly on first build)

This is the standard community workaround for WinAppSDK 1.5+ when
building outside Visual Studio. It is documented here and in
`ARCHITECTURE.md` so future maintainers understand the design.

## How the build flow works

1. `dotnet build` on any project in the solution
2. `Directory.Build.props` registers `UsingTask` overrides for
   AppxPackage / Pri tasks, pointing at the stub assembly.
3. `Directory.Build.targets` first ensures the stub assembly is built
   (no-op if already present), then the rest of the build proceeds.
4. WinAppSDK's PRI generation / packaging targets run but find their
   tasks via the stub and no-op cleanly.
5. The XAML compiler (`XamlCompiler.exe`) processes WinUI XAML.

## Build commands

| Purpose | Command |
|---|---|
| Restore | `dotnet restore Encomm.sln` |
| Build Debug | `dotnet build src/Encomm.Browser.App/Encomm.Browser.App.csproj -c Debug` |
| Build Release | `dotnet build src/Encomm.Browser.App/Encomm.Browser.App.csproj -c Release` |
| Run | `dotnet run --project src/Encomm.Browser.App/Encomm.Browser.App.csproj -c Debug` |
| Test | `dotnet test tests/Encomm.Browser.Tests/Encomm.Browser.Tests.csproj` |

The PowerShell scripts in `scripts/` are thin wrappers around these.

## Known limitations of the current setup

- The XAML compiler still requires .NET Framework tooling paths
  (Windows-only). The CLI build does work, but a Visual Studio install
  would make the WinUI designer available.
- `Microsoft.Web.WebView2` is pinned to a version compatible with
  `Microsoft.WindowsAppSDK 1.7`. The `CreateAsync` overload signature
  for `CoreWebView2Environment` varies across SDK versions, so the
  engine adapter probes the available signatures at runtime via
  reflection.
- The stub task assembly is the only piece of generated build output
  that is checked into the build process (it gets re-generated from
  source if missing).

## What was actually used during the Phase 1 build (anonymized)

- Windows 11 Pro 24H2 (build 26100.x)
- .NET SDK 9.0.315
- Microsoft.WindowsAppSDK 1.7.250606001
- Microsoft.Web.WebView2 1.0.2903.40
- CommunityToolkit.Mvvm 8.4.0
- Microsoft.Data.Sqlite 9.0.0
- System.Security.Cryptography.ProtectedData 9.0.0

The full transitive NuGet graph is reproducible from `Encomm.sln`.