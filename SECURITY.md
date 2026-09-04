# Security

## Threat model assumptions

* Web content is **fully untrusted**.
* The renderer process (WebView2) is compromised-by-default for the
  purposes of privilege boundary design.
* Local machine is shared with the user account; secrets stored locally
  must be protected from other local users but assume compromise of
  another process running as the same user is possible.

## Trust boundaries

```
+--------- untrusted --------+      +------- trusted (native host) ------+
|                            |      |                                    |
|   Web content / scripts    | <--> |  Browser host / services          |
|                            |      |   (Tabs, Shield, AI, Storage,     |
|                            |      |    Settings, Security)            |
+----------------------------+      +------------------------------------+
```

Any data that crosses from left to right must be:

* size-bounded
* validated
* never trusted as code

## WebView2 flags

The following flags are NEVER used, in any build:

* `--disable-web-security`
* `--no-sandbox`
* `--ignore-certificate-errors`
* `--allow-running-insecure-content` (permissive site)

## Permission handling

* **Camera / Microphone / Geolocation / Notifications** — explicit
  per-request UI prompt. Default is deny.
* **Clipboard** — explicit user gesture required to read.
* **Popups / new windows** — intercepted, displayed in a new tab only
  after user intent (a click on a link with `target=_blank` qualifies).
* **Downloads** — explicit per-download confirmation, scanned directory,
  filename sanitized.

## Secret handling

* API keys are stored encrypted with DPAPI (`CurrentUser` scope).
* Optionally mirrored to Windows Credential Manager.
* Secrets NEVER appear in:
  * `appsettings*.json`
  * SQLite database
  * log files
  * crash dumps
  * git history
  * the on-screen UI after being saved
* The Settings UI displays only that a secret is "configured", not the
  value. "Reveal" or "Test connection" actions are explicit and
  short-lived.

## Request blocking (Shield)

* Built-in safe rule set is hand-written and ships with the product for
  Phase 1.
* Third-party filter lists are not bundled and will not be added until a
  license review is completed and the update channel is signed and
  validated.
* Blocked requests return a synthetic empty response, are not silently
  redirected to third-party hosts, and are counted in the Shield panel.

## Reporting a vulnerability

Please report security issues to:

* **security@encomm.example** (placeholder for the project's actual
  security contact, to be replaced before any public release)

Do not file public GitHub issues for suspected security defects until a
patch is available.