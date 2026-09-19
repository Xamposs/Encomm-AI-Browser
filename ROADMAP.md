# Roadmap

This document describes the planned phases for Encomm-AI-Browser. Dates
are intentionally not attached — phases are milestone-based.

## Phase 1 — Windows Native Foundation

Goal: a real, runnable Windows browser with the architectural skeleton
that makes the rest of the product possible.

Deliverables (this phase):

* .NET 9 + WinUI 3 + WebView2 host
* Engine abstraction + WebView2 adapter
* Tab model with Live / Warm / Ghost lifecycle
* Workspaces
* SQLite persistence (versioned schema)
* Encomm Shield request-blocking pipeline with safe built-in rules
* Optional AI via OpenAI-compatible provider + secure secret storage
* Everyday Mode + Developer Mode
* Keyboard shortcuts, NTP, error states, logging
* Test suite + build/run/test scripts

Status: **in progress** (see `docs/PHASE_1_REPORT.md`).

## Phase 2 — Advanced Ghost Tabs + Performance

* Smarter heuristics for `Live -> Warm -> Ghost` demotion
* Scroll position restoration where safely possible
* Per-tab memory accounting
* Tab grouping UI improvements
* "Save memory" preset tuning

## Phase 3 — Shield production engine

* License-reviewed, signed third-party filter list integration
* Tracker / fingerprinting / cookie annoyance handling
* URL tracking-parameter stripping
* Per-site shield statistics

## Phase 4 — Workspace Intelligence

Status: **MVP + first Canvas delivered in Phases 3B/3C** — bounded context
builder, structured results, real source traceability, page/workspace
summaries, comparisons, extraction and ask-across-workspace, plus the first
ENCOMM Canvas: an intent-driven generated workspace (comparison table or
evidence list) with per-cell citations, persisted per workspace and rendered
as a native surface. See `docs/PHASE_3B_REPORT.md` and
`docs/PHASE_3C_REPORT.md`.

Remaining work under this phase:

* a Canvas gallery (list/reopen past canvases) and richer table layout
* content coverage for cold tabs without waking them (invisible workers)
* "Intent workspaces" — a natural-language description creates a focused
  workspace (tabs as well as a canvas)
* multi-intent canvases (compare + evidence + plan in one workspace)

## Phase 5 — Browser Agents

* Tab-aware agents operating inside the browser's own UI
* Cross-tab workflows with explicit user control
* Audit trail for every privileged agent action

## Phase 6 — Developer Workspace

* First-class Developer Mode: source view, console, network, agent
  inspector, Git integration, runtime
* Layout designed around serious developer workflows, not stripped from
  the everyday UI

## Phase 7 — CEF / custom Chromium engine

* New `Engine.Cef` adapter behind the same `IBrowserEngine` interface
* Replace WebView2 without rewriting the product
* Eventually: custom Chromium-based ENCOMM engine

## Phase 8 — Public Alpha / Updater / Signing

* Authenticode signing pipeline
* Auto-update mechanism with integrity verification
* Public Alpha release