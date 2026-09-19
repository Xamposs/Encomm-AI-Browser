# Phase 3C Report — ENCOMM Canvas (browser-generated workspaces, v0)

Builds directly on Phase 3B. Where 3B answered a question, 3C turns a
stated **intent** plus this workspace's sources into a persistent,
task-specific, **native** workspace artifact — not a chat transcript and
not a web page.

## What shipped

**1. The Canvas model** (`Encomm.Browser.AI/Intelligence`)

* `AICanvas` — one generated workspace: kind, title, summary, columns,
  rows, uncertainties, sources, status, timestamps.
* `CanvasCell` — a single claim with its own citations, so "price = 999"
  traces to the exact source it came from, not merely to "the workspace".
* `CanvasParser` — deliberately forgiving, never throws. Degrades:

| Model returned | Canvas renders as |
|---|---|
| explicit table (`columns` + `rows.cells`) | Comparison table |
| rows with differing facts, no columns | Comparison table (columns synthesised) |
| rows with facts that do **not** differ | Evidence list (no fake table) |
| rows with plain text only | Evidence list |
| summary only | Summary |
| anything else | raw text becomes the summary |

* `CanvasPayload` — one opaque JSON payload the storage layer never
  interprets, so the canvas shape can evolve without migrations.

**2. A native Canvas surface** (`Dialogs/CanvasDialog`)

Intent box, status, comparison **table built in code** (XAML cannot
declare dynamic columns), evidence rows, per-cell citation chips, "Worth
checking" uncertainties, source list, copy (citations included), delete
(a canvas is a regenerable derived artifact — no confirmation dialog, and
pages are never touched). Empty state offers intent suggestions so it is
never a dead end. Zero WebViews.

**3. Persistence** (`canvases` table, schema v2 → v3)

Latest canvas per workspace loads on open, so intent survives a restart.
Deleting a workspace deletes its canvases. Only **usable** canvases are
stored: "not configured", "no sources" and failure are transient UX
states, not workspace content.

**4. Entry points**

Main menu → *ENCOMM Canvas…*, and the AI surface gains *"Turn into a
Canvas"* so an answer can become a reusable workspace artifact with the
question carried across as the intent.

**5. Same discipline as 3B**

Reuses the bounded context builder, citation resolution and renderer
policy: **no renderer is allocated for tabs the user is not looking at**,
native/blank tabs are never sent, and the model is told source text is
untrusted.

## Tests

**148/148** (was 130). New `CanvasTests` (18):

* parser: real table with per-cell citations, evidence fallback,
  column synthesis from differing facts, **no fake table when attributes
  agree**, explicit kind honoured, invented citations dropped (with
  bracketed labels accepted), unstructured output kept as a summary;
* payload: round-trip, never throws on bad input;
* persistence: per-workspace isolation, newest-first, delete, workspace
  deletion cascade, and **v2 → v3 migration with no data loss** (legacy
  workspace/tab rows verified intact);
* lifecycle: usable canvases persist, unusable ones never do, cold tabs
  are never woken, provider failure never throws.

## Live verification

* Release build launched; startup log clean; no unhandled-exception
  sidecar.
* UIA: main menu → ENCOMM Canvas opened; empty state rendered with all
  four intent suggestions; choosing a suggestion populated the intent and
  generated a canvas; with no provider configured the surface showed the
  plain-language notice (no provider jargon) and **no canvas was stored**.

## Honest limitations

* The full generation path against a **real model** was not exercised live
  in this environment (no provider is configured and credentials must
  never be committed). It is covered by recording-router tests.
* The comparison **table** path is exercised by tests and by XAML/code
  construction, but its on-screen pixels were not captured here (no model
  was available to produce a table live). The dialog and empty/unconfigured
  paths were verified on-screen via UIA.
* Table columns cap at the model's declared columns plus a "Detail"
  catch-all, and are fixed-width (170); very wide canvases scroll
  vertically. This is a v0 layout, not the final Canvas experience.
* No true multi-canvas gallery yet: `CanvasService.List` exists, but the
  surface opens the latest canvas (the rest is a small follow-up).

## Files

```
src/Encomm.Browser.AI/Intelligence/AICanvas.cs
src/Encomm.Browser.AI/Intelligence/CanvasParser.cs
src/Encomm.Browser.AI/Intelligence/CanvasPayload.cs
src/Encomm.Browser.App/Services/CanvasService.cs
src/Encomm.Browser.App/Services/AIService.cs            (canvas contract)
src/Encomm.Browser.Core/Storage/BrowserPersistenceService.cs (schema v3)
src/Encomm.Browser.App/Dialogs/CanvasDialog.xaml*
src/Encomm.Browser.App/Dialogs/AiResultViews.cs
src/Encomm.Browser.App/Dialogs/AICommandDialog.*        (Canvas bridge)
src/Encomm.Browser.App/MainWindow.*                     (entry point)
src/Encomm.Browser.App/App.xaml.cs                      (DI)
tests/Encomm.Browser.Tests/CanvasTests.cs
```

## Next milestone candidates

* The Canvas gallery (list/reopen past canvases) and richer table layout.
* Shield production maturity (handoff Phase 4) — a strategic choice, as it
  trades product-polish time against a different part of the roadmap.