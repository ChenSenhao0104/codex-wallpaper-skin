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

## ADR-007 — The injected layer cannot reach an HTTP frame stream

**Status:** accepted (measured on Windows build 26200, Codex 2026-09-20)

Issue #1 section 3 asks to avoid per-frame JPEG/Base64 transport. A loopback MJPEG server was implemented, unit-tested (loopback-only bind, per-session token, two served paths, multipart framing, latest-wins backpressure) and wired behind a page-side reachability probe that could switch transports at runtime and fall back to CDP.

Measured against a live Codex page, the probe returned `cdp:probe-refused/fetch-refused`: a Codex `app://` page refuses **both** an `<img>` load and a `fetch` to `http://127.0.0.1:<port>`, including a same-machine loopback with a per-session token. An HTTP frame stream is therefore unreachable from the injected layer, and no `blob`/`data` alternative changes that, because the channel itself is blocked rather than the encoding.

Decision: the MJPEG server and its wiring were removed rather than shipped as network code that can never run on the supported platform. Consequences for the transport axis:

- the CDP channel remains the only transport, so the injected presentation layer still performs no script-initiated network access of its own, and the injected runtime keeps its "no `fetch`, no `XMLHttpRequest`" property, which the self-tests assert;
- transport cost must be attacked by sending **smaller or partial payloads over CDP** (for example WebP at equal quality, or changed-region updates) rather than by changing the channel;
- if a future Codex build allows loopback requests from `app://`, the reachability-probe design recorded in this decision is the pattern to reintroduce, and it must stay feature-detected rather than assumed.
