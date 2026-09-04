# Third-Party Notices

This document lists third-party software used by **Encomm-AI-Browser**.

Each entry includes the component, version, license, and the purpose for
which it is used.

Last updated: Phase 1 development snapshot.

---

## Runtime / OS

| Component | Version | License | Purpose |
|---|---|---|---|
| Microsoft .NET | 9.0 | MIT | Application runtime |
| Microsoft.WindowsAppSDK | 1.7.x | MIT | Native Windows UI framework |
| Microsoft.Web.WebView2 | 1.0.x | BSD-style / Microsoft Software License Terms | Rendering adapter (Phase 1) |

## NuGet dependencies (project-level)

| Package | License | Project | Purpose |
|---|---|---|---|
| Microsoft.WindowsAppSDK | MIT | App, Engine adapter | WinUI 3 / native host |
| Microsoft.Web.WebView2 | Microsoft | Engine.WebView2 | Phase 1 rendering adapter |
| Microsoft.Extensions.DependencyInjection | MIT | App, Core | Composition root |
| Microsoft.Extensions.Logging | MIT | Core, App | Logging abstraction |
| Microsoft.Extensions.Hosting | MIT | App | Hosted service lifecycle |
| CommunityToolkit.Mvvm | MIT | App | Lightweight MVVM helpers |
| Microsoft.Data.Sqlite | MIT | Core | Local persistence |

## Built-in default filter rules

The Phase 1 build ships with a small, hand-crafted built-in rule set used
solely as a sanity check for the Shield blocking pipeline. It is **not**
derived from EasyList, uBlock, Brave, or any other third-party filter list,
and no third-party filter lists are bundled or fetched by default.

## Fonts, icons, assets

The application uses a temporary text-based **ENCOMM** wordmark while the
official brand assets are unavailable. No third-party fonts or icon sets are
bundled in Phase 1.

## Notes

* No GPL/AGPL-licensed components are linked into the proprietary ENCOMM
  core. Any future addition of an MPL/LGPL component would be evaluated for
  isolation and documented here.
* Third-party filter lists will only be added after license review and a
  signed/validated update mechanism, documented in `SECURITY.md` and
  `THIRD_PARTY_NOTICES.md`.