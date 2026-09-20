# Architecture Decisions

This log records decisions that affect more than one implementation branch. Add a dated entry before changing a shared boundary.

## ADR-001 — Reversible presentation layer

**Status:** accepted

Codex Wallpaper Skin injects a reversible presentation layer through a verified local CDP endpoint. It does not modify or replace files in the official Codex package and does not read conversation content.

## ADR-002 — Native Wallpaper Engine as the high-fidelity backend

**Status:** accepted direction

Wallpaper Engine remains responsible for rendering complex Scene and controlled Web wallpapers. The companion captures native output and forwards bounded input instead of continuously adding per-wallpaper shader patches. The safe in-browser scene renderer remains a compatibility fallback, not the universal backend.

## ADR-003 — Product convergence from parallel implementations

**Status:** accepted

Parallel Codex- and DeepSeek-assisted branches explore solutions independently from the same specification and baseline. Integration is component-based and product-first. The final implementation may select either candidate, combine compatible strengths, or reject both.

## ADR-004 — No Workshop media in source control

**Status:** accepted

Real Workshop projects may be used for local manual validation but are never copied into the repository, CI artifacts, Issues, or releases without explicit redistribution rights. Synthetic or appropriately licensed scenes are used for automation.

## ADR-005 — Application wallpapers remain unsupported

**Status:** accepted

Application wallpapers execute arbitrary programs and remain outside the product safety boundary. Discovery and apply paths must reject them.

## ADR-006 — Human-controlled releases

**Status:** accepted

Automation may build and test candidates, but every GitHub Release is created as a draft and published only after explicit maintainer approval. Code signing is deferred until a later distribution stage.

## ADR-007 — v0.4 GPU media is carried by a bounded CDP fragment channel, not a loopback socket

**Status:** accepted for the DeepSeek candidate; flagged for maintainer review at convergence

Issue #2 asks for "a bounded local transport" and permits networking when it is "loopback-only". A loopback HTTP/WebSocket media server was the first design considered for `deepseek/v0.4-gpu-stream`. It was rejected on measured evidence from a live Codex page, not on preference:

| Channel | Result on the live Codex app page |
| --- | --- |
| `fetch('http://127.0.0.1:<port>/…')` | Blocked. Console: `Connecting to 'http://127.0.0.1:<port>/a' violates the following Content Security Policy directive: "connect-src 'self' https://ab.chatgpt.com … wss://ws.chatgpt.com"` |
| `fetch('http://localhost:<port>/…')` | Blocked by the same `connect-src` directive |
| `new WebSocket('ws://127.0.0.1:<port>/…')` | Blocked; `connect-src` has no loopback source |
| `<video src="http://127.0.0.1:<port>/…">` | Blocked. `media-src 'self' app: blob: data:` excludes `http:` |
| Blob-URL `Worker` that fetches loopback | Blocked; the worker inherits the document policy |
| Blob-URL `iframe` that fetches loopback | Blocked; the nested document inherits `script-src` and `connect-src` |
| `Page.setBypassCSP(true)` on the already-loaded page | No effect. The bypass applies to documents created after the call, so it would require reloading the Codex app page (`Page.reload`) or relaunching Codex with web security disabled |
| `MediaSource` + `URL.createObjectURL(mediaSource)` | Allowed. `media-src` includes `blob:`, and `avc1.*` is reported supported by `MediaSource.isTypeSupported` |

Relaunching Codex or disabling its web security to win a transport argument would trade a real, user-visible security boundary for a local optimization, and reloading the app page destroys the session the user is looking at. Neither is acceptable for a product that injects into an AI desktop client.

**Decision.** The v0.4 production path uses Media Foundation for capture-side H.264 encoding and an MSE `<video>` surface for hardware decode, with the encoded fragments carried by the existing authenticated CDP session instead of a socket. Consequences:

- No listening socket, no URL capability token, and no per-frame JPEG, Base64 or DOM work. Per-frame CDP evaluation is replaced by one bounded fragment push per encode fragment (~50–100 ms), so CDP message volume drops by roughly two orders of magnitude versus the v0.3 JPEG path while CDP still carries bytes.
- This is a deviation from the literal wording "CDP … must not carry normal video frames" in Issue #2. The deviation is recorded here, reported in the acceptance report, and reversible: the fragment push is behind a transport seam, so a loopback transport can be enabled unchanged if a future Codex build relaxes `connect-src`.
- The reduced-frame-rate JPEG/CDP compatibility backend is retained and is selected automatically whenever the GPU path is unsupported, fails to initialise, or loses its stream identity.
