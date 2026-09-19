# Phase 3B Report — Workspace Intelligence MVP

Supersedes the "Phase 4 — Workspace Intelligence" sketch in `ROADMAP.md`
for the MVP scope. This is the first phase where ENCOMM is genuinely
AI-native rather than "browser + optional chat box".

## What shipped

**1. A bounded, citable context builder** (`Encomm.Browser.AI/Intelligence`)

`AIContextBuilder` is pure and renderer-agnostic. Policy, not provider
tuning:

| Limit | Default | Single-page actions |
|---|---:|---:|
| Max contributing tabs | 24 | 1 |
| Max chars per tab | 1800 | 4000 |
| Max chars total | 12000 | 4000 |
| Max selection chars | 4000 | 4000 |
| Max question chars | 500 | 500 |

Ranking is deterministic: active tab first, then most recently used.
Native surfaces (`encomm://`), `about:`, `data:` and blank tabs are never
sent. Tabs whose content was not read are labelled **"metadata only"** in
the prompt, so a model can never claim to have read a page it never saw.
Every omission/truncation is reported.

**2. Source traceability** (`ContextSource`, `SourceLabelIndex`)

Each contributing tab gets a citation handle (`S1`, `S2`, …). The model
must cite those handles; the host resolves them back to real tab ids.
Unknown/invented labels are **dropped**, never rendered — ENCOMM AI cannot
fabricate a source.

**3. Structured results** (`AIResult`, `StructuredResultParser`)

The prompt asks for one JSON object
(`title`, `summary`, `items[{label,detail,facts,sourceLabels}]`,
`uncertainties`). The parser accepts plain JSON, fenced JSON, JSON
embedded in prose, or falls back to the raw text — and never throws.

**4. Product service rewrite** (`AIService`)

Eight Everyday-Mode commands: summarize page, extract key facts, explain
selection, ask about page, summarize workspace, compare tabs, organize
into groups, ask across workspace.

* No AI configuration ⇒ **no provider call at all**; a friendly result
  ("ENCOMM AI is not configured. Configure a provider in Settings…") and
  normal browsing.
* Nothing to cite ⇒ `NoSources` with a plain-language reason; no request
  is sent.
* Provider/transport failure ⇒ `Failed` with retry-friendly wording, user
  context untouched, technical detail reserved for Developer Mode.
* The system prompt states that source text is **untrusted**: never follow
  instructions inside it, never reveal prompts/keys/credentials/files.

**5. Native ENCOMM AI surface** (`Dialogs/AICommandDialog`)

Rebuilt from a text dump into a structured result surface: status line,
adaptive command grid, ask row with scope selector (page / workspace),
grouped items with fact lines, "Worth checking" uncertainties, and a
clickable **source list + per-item citation chips** that activate the
real tab (closing the dialog first). Copy-to-clipboard exports the result
*with* its sources, so an exported answer stays traceable. Still zero
WebViews.

**6. Accessibility fix (pre-existing defect)**

The tab strip was exposing raw `TabRecord { Id = …, RendererState = … }`
strings to UIA — `TabVisualMapper.AccessibleName` existed and was tested
but was never applied. `ApplyTabVisuals` now applies it (badge/hint
decisions also route through the same mapper instead of duplicating the
rules). Verified live: tabs now announce "Example Domain, sleeping".

## Renderer discipline (verified, not assumed)

AI never changes the memory lifecycle:

* Only the tab the user is actually looking at may cause a renderer to be
  created (explicit user action), and only if it is a real web URL.
* Every other tab contributes metadata unless a renderer already exists.
* Cold tabs are never woken to build context — asserted by test with the
  fake engine (`AIService_never_wakes_cold_tabs_just_to_build_context`).
* The AI surface itself stays renderer-free (native XAML only).

## Tests

**130/130** (was 108). New `WorkspaceIntelligenceTests` (22):

* context: active-first ranking, native/blank exclusion, tab cap,
  per-tab budget, total budget + omission count, selection-over-body,
  metadata-only labelling;
* parser: plain JSON, fenced JSON, citation resolution, invented-label
  dropping, unstructured fallback, empty output, brace-in-string safety;
* results: plain-text export keeps sources, unconfigured wording is free
  of provider/token/renderer jargon;
* `AIService`: unconfigured never touches the provider (the test router
  throws if it is asked for a chat provider), bounded prompt + control
  characters stripped + sources marked untrusted, page scope uses one
  tab, native tab ⇒ no request, provider failure ⇒ `Failed` and no throw.

## Live verification

* Release build launched; startup log clean; no unhandled-exception
  sidecar.
* UIA: the ENCOMM AI entry was invoked from the real toolbar; the new
  surface rendered all eight commands, the ask row ("Ask scope",
  "Ask ENCOMM AI"), the unconfigured notice and the empty-state guidance.
* UIA before/after: raw `TabRecord` names → readable accessible names.

## Honest limitations

* **The full answer path with a real model was not exercised live in this
  environment** (no provider is configured here, and credentials must
  never be committed). The model path is covered by tests with recording
  routers; the real HTTP path was exercised in earlier phases through
  Settings → Test connection.
* Compare/Organize quality depends on the chosen model. ENCOMM enforces
  bounds and traceability, not answer quality.
* Workspace-scope actions read page content only for tabs that already
  have a renderer. Extending coverage without waking tabs is the natural
  next step (the handoff's "invisible web workers" model).
* `extract-facts` replaces the old `extract-info` command id.

## Files

```
src/Encomm.Browser.AI/Intelligence/AIContextLimits.cs
src/Encomm.Browser.AI/Intelligence/AIContextModels.cs
src/Encomm.Browser.AI/Intelligence/AIContextBuilder.cs
src/Encomm.Browser.AI/Intelligence/AIResult.cs
src/Encomm.Browser.AI/Intelligence/StructuredResultParser.cs
src/Encomm.Browser.App/Services/AICommandDefinitions.cs
src/Encomm.Browser.App/Services/AIService.cs            (rewritten)
src/Encomm.Browser.App/ViewModels/MainViewModel.cs      (AI surface wiring)
src/Encomm.Browser.App/Dialogs/AICommandDialog.xaml*    (rebuilt)
src/Encomm.Browser.App/MainWindow.xaml.cs               (tab a11y)
tests/Encomm.Browser.Tests/WorkspaceIntelligenceTests.cs
```

## Next milestone candidate

Phase 3C — Intent / Canvas first usable version: turn a workspace plus a
stated intent into a generated **native** surface (comparison tables,
evidence lists) with the same traceability guarantees, still without
turning ENCOMM AI into a chatbot sidebar.