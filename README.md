# Encomm-AI-Browser

> An AI-native browser in which intelligence is part of the browser architecture itself.

Encomm-AI-Browser is a Windows desktop browser built on .NET 9, WinUI 3,
and the Microsoft Edge **WebView2** runtime — with a clean engine
abstraction so that the rendering layer can later be replaced by CEF or a
custom Chromium-based ENCOMM engine without rewriting the product.

The product pillars are:

* a real, runnable Windows browser
* a strict rendering-engine abstraction (`IBrowserEngine`)
* a true **Tab Lifecycle** with **Live / Warm / Ghost** states
* **Workspaces** that organize browsing around intent
* **Encomm Shield**, a real request-blocking pipeline
* **optional** AI via an OpenAI-compatible provider with secure local
  secret storage (DPAPI / Windows Credential Manager)
* **Everyday Mode** for normal users and **Developer Mode** for diagnostics
* privacy-first, local-first defaults; zero mandatory analytics

The ordinary user should never need to know what a "token" is.

## Status

**Phase 3B — Workspace Intelligence MVP** (development snapshot).

Browsing, Ghost tabs, workspaces, Shield and the branded shell are in
place (Phases 1 → 3A). Phase 3B makes ENCOMM AI-native: page and
workspace summaries, comparisons, key-fact extraction and Q&A — each
answered from a **bounded, cited** source set, rendered on a native
ENCOMM surface, and fully optional. Browsing remains excellent with AI
disabled or unconfigured. See `docs/PHASE_3B_REPORT.md`.

Current test suite: **130/130 passing**.

The repository is public; the source remains **proprietary** to ENCOMM
(public source ≠ open-source license).

## Requirements

* Windows 11 (x64)
* .NET SDK 9.0
* Microsoft Edge **WebView2** Runtime (already shipped with current
  Windows 11 / Edge)
* Windows App SDK 1.7 runtime (auto-installed via MSIX dependency; for
  unpackaged use, install the redistributable)

## Build

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1
```

## Run

```powershell
./scripts/run.ps1
```

## Test

```powershell
./scripts/test.ps1
```

## Architecture

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the full layering.

```
UI (WinUI 3)
   |
Tab / Workspace / AI / Shield / Settings  (product services)
   |
IBrowserEngine  <-- engine abstraction
   |
WebView2 adapter   (Phase 1)
   |
CEF / custom Chromium   (future)
```

## Roadmap

See [`ROADMAP.md`](ROADMAP.md).

## Security

See [`SECURITY.md`](SECURITY.md).

## License

Original ENCOMM source code is proprietary. See `LICENSE` and
`THIRD_PARTY_NOTICES.md`.