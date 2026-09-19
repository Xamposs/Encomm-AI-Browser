# Architecture

## Goals

* The browser must remain excellent even when AI is disabled.
* The rendering layer must be replaceable. WebView2 is **Phase 1** only.
* Memory efficiency is a first-class product feature. "100 tabs" must not
  mean "100 live WebView2 controllers".
* AI is an enhancement, not a dependency. The browser must launch and
  function with zero AI configuration.
* Privacy first. No mandatory telemetry. No cloud sync in Phase 1.

## High-level layering

```
+---------------------------------------------------------+
|                        UI (WinUI 3)                     |
+---------------------------------------------------------+
                          |
                          v
+---------------------------------------------------------+
| Product services                                         |
|   Tabs   Workspaces   Shield   AI   Settings   Memory    |
+---------------------------------------------------------+
                          |
                          v
+---------------------------------------------------------+
|              IBrowserEngine  /  IBrowserView             |
|              (engine abstraction — no WebView2 here)     |
+---------------------------------------------------------+
                          |
                          v
+---------------------------------------------------------+
|           WebView2 Adapter  (Phase 1 only)               |
|           (the only project that references              |
|            Microsoft.Web.WebView2 directly)              |
+---------------------------------------------------------+
                          |
                          v
                  Microsoft Edge WebView2
```

## Project map

| Project | Responsibility |
|---|---|
| `Encomm.Browser.Engine.Abstractions` | `IBrowserEngine`, `IBrowserView`, events, types. No WebView2. |
| `Encomm.Browser.Engine.WebView2` | WebView2 adapter. Only project that references `Microsoft.Web.WebView2` directly. |
| `Encomm.Browser.Core` | Domain models, options, logging, utilities, no-UI services. |
| `Encomm.Browser.Tabs` | Tab domain, lifecycle states, manager, recently-closed. |
| `Encomm.Browser.Workspaces` | Workspace model + manager. |
| `Encomm.Browser.Memory` | Process / working-set / tab-state telemetry. |
| `Encomm.Browser.Shield` | Request blocking pipeline, rule providers, default safe rule set. |
| `Encomm.Browser.AI` | Provider abstractions, OpenAI-compatible provider, mock provider. |
| `Encomm.Browser.Settings` | Settings model + persistence + secret storage (DPAPI / CredMan). |
| `Encomm.Browser.Security` | Cryptographic / boundary helpers, content sanitization utilities. |
| `Encomm.Browser.Developer` | Developer Mode services: diagnostics, exposed APIs. |
| `Encomm.Browser.App` | WinUI 3 host, composition root, XAML, MVVM. |
| `tools/MemoryProbe` | Standalone console probe that measures working set at startup. |

## Browser engine abstraction

The product never directly touches WebView2. It interacts with:

* `IBrowserEngine` — process-wide factory / shared environment
* `IBrowserView` — per-tab renderer handle with lifecycle events

Capabilities surfaced:

* `Navigate`, `Reload`, `Stop`, `GoBack`, `GoForward`
* `CanGoBack`, `CanGoForward`
* `Suspend`, `Resume`, `Destroy`
* `CapturePreview`
* `ExecuteScript`, `ExtractPageContext`
* `GetTitle`, `GetUrl`
* Events: `NavigationStarting`, `NavigationCompleted`, `Loading`,
  `TitleChanged`, `FaviconChanged`, `Audio`, `Download`, `Permission`,
  `NewWindow`, `Blocked`
* `OpenDevTools`

Replacing WebView2 with CEF or a custom Chromium-based engine means
shipping a new adapter project and swapping DI registration. UI,
Workspaces, Tabs, Shield, AI, Settings remain untouched.

## Tab lifecycle

```
                 +-----------+
                 |   GHOST   |   <-- rendering context destroyed
                 +-----+-----+       (only metadata kept)
                       |
       (selected again, restored)
                       v
                 +-----------+
                 |   WARM    |   <-- renderer suspended where supported,
                 +-----+-----+       full snapshot retained
                       |
            (selected, focused, used)
                       v
                 +-----------+
                 |   LIVE    |   <-- full active browsing context
                 +-----------+
```

* **GHOST** is the critical state for memory efficiency. A Ghost Tab MUST
  release its renderer entirely. Reopening navigates fresh.
* **WARM** is a best-effort intermediate state using WebView2's
  `TrySuspendAsync` when available, with graceful fallback to GHOST.
* **LIVE** is the only state where the WebView2 controller is fully alive.
* Tabs that are pinned, audio-playing, downloading, mic/cam-using,
  "Keep Awake", or recently interacted with are protected from automated
  demotion.

## Workspaces

Workspaces are user-named logical groups of tabs. Switching workspace
encourages aggressive renderer release in non-active workspaces. Workspace
data is persisted in SQLite and restored on startup.

## Encomm Shield

```
Request (WebView2 WebResourceRequested)
    |
    v
IRequestBlocker.ShouldBlock(uri, type)
    |
    +-- IFilterRuleProvider (local rules + safe built-in defaults)
    |
    +-- allow / block decision
    |
    v
WebView2 navigates OR returns synthetic empty response
```

* Built-in safe rule set is hand-written, NOT derived from third-party
  lists, and ships with the product for sanity testing only.
* Third-party filter list integration is intentionally deferred until a
  license-reviewed, signed update path exists.

## AI

* Optional. Disabled by default. Browser launches with zero AI config.
* `IAIProvider` / `IChatProvider` / `IEmbeddingProvider` /
  `IModelRouter`.
* Phase 1 ships:
  * `OpenAICompatibleProvider` — base URL, API key, model name, optional
    custom headers. Works with OpenAI, OpenRouter, LM Studio, llama.cpp
    servers, vLLM, etc.
  * `MockAIProvider` — for tests and offline use.
* **No API key in plaintext anywhere.** Keys are encrypted via DPAPI and
  optionally mirrored to Windows Credential Manager. Secrets never enter
  SQLite, settings files, logs, crash dumps, or git.

### Phase 3B — Workspace Intelligence (`Encomm.Browser.AI/Intelligence`)

```
UI action (native ENCOMM surface)
  -> AIService              (App layer: command catalogue, renderer policy)
  -> AIContextBuilder       (bounded + ranked + citable sources)
  -> IModelRouter -> provider
  -> StructuredResultParser (AIResult: items, facts, sources)
  -> native result surface with clickable citation chips
```

* **Bounded by policy**, never by hope: max tabs, max chars per tab, max
  total chars, max question length (`AIContextLimits`). Native/blank tabs
  are never sent; tabs whose content was not read are labelled
  "metadata only".
* **Traceable**: every source carries a citation handle (`S1`, `S2`, …)
  that the host resolves back to a real tab (`SourceLabelIndex`).
  Invented labels are dropped rather than displayed.
* **Structured**: models are asked for one JSON object; the parser accepts
  plain/fenced/embedded JSON and never throws
  (`StructuredResultParser`). Raw text is preserved.
* **Renderer discipline**: the AI path never allocates a renderer merely
  to build context. Only the tab the user explicitly targets may be
  restored; everything else contributes metadata unless it already has a
  renderer. AI therefore never changes the Live/Warm/Ghost lifecycle.
* **Failure experience**: unconfigured ⇒ no provider call and a plain
  language notice; provider failure ⇒ retry-friendly result with technical
  detail reserved for Developer Mode.
* **Untrusted input**: page text is treated as data, not instructions; the
  system prompt forbids following in-page directions and forbids revealing
  prompts, keys, credentials or local files.
* See `docs/PHASE_3B_REPORT.md`.

## Storage

* Local embedded SQLite (Microsoft.Data.Sqlite).
* Versioned schema (`schema_version` table). Migrations applied on
  startup.
* Stores: workspaces, tabs, ghost metadata, settings, recently closed,
  history metadata (URL + title + last visit only).
* **Does not** store full page text, secrets, or unbounded content.

## Privacy

* No telemetry to ENCOMM servers. None. Phase 1 has no analytics endpoint.
* No "send usage data" toggle. There is nothing to toggle.
* AI page context is only sent to a remote model after an explicit user
  action ("Summarize this page", "Ask about this page", etc.).
* Cloud sync, account linking, and remote history are deferred.

## Security boundary

* The renderer (WebView2 process) is treated as fully untrusted.
* Native host APIs are never exposed to web content directly. Any
  communication goes through narrow, validated message channels.
* Page context extraction is rate-limited and size-bounded.
* Permission requests (camera, mic, geolocation, notifications, clipboard)
  are explicit per request.
* No `disable-web-security`, no `--no-sandbox`, no certificate-error
  suppression.
* See `SECURITY.md`.

## Future Chromium migration

When the ENCOMM custom engine is ready:

1. Add `Encomm.Browser.Engine.Cef` (or a custom C# wrapper around CEF).
2. Re-implement `IBrowserView` against the new adapter.
3. Register the new adapter in `App.xaml.cs` composition root.
4. UI / Tabs / Workspaces / Shield / AI / Settings remain untouched.

The discipline that protects this is: **no project outside the adapter
references the renderer SDK directly.**