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

## ADR-008 — Posted mouse messages do not drive native scene pointer state

**Status:** accepted as a measured finding; remedy not yet chosen (Windows build 26200, Codex 2026-09-20)

Hard gate 2 requires a confirmed interactive scene (Saki Tenma, `2914257158`) to respond to pointer movement in the **native** Wallpaper Engine path. The companion forwards pointer state by posting `WM_MOUSEMOVE` / button messages to the private `-playInWindow` render window. That mechanism was assumed to be sufficient; it was never verified until now.

Measurement: after applying the scene natively, the harness sampled the rendered output in three phases — pointer parked, a wide hover sweep, and a wide sweep with the left button held — while differencing a water band and a static control band. Pointer dispatch was forced even though the Codex page reported itself hidden, so the test could not be voided by the hidden-page skip.

- quiet water band frame-to-frame pixel delta: 7.53
- hover sweep: 7.03
- button-held drag sweep: 7.13
- static control band: 0.000 in every phase (so the instrument can tell moving from static regions)
- input messages posted to the render window: **73 accepted, 0 refused**

Conclusion: input delivery works and the renderer accepts the messages, but the scene does not react. The same project *does* expose a pointer-reactive water effect — the browser safe renderer reports `pointerRippleLayers: 1, pointerRipplePassCount: 10` for it — so the missing reaction is in the native path, not in the scene.

Consequences:

- **Hard gate 2 is not satisfied for the native backend.** The earlier claim in `docs/tasks/ISSUE-1-DEEPSEEK-EVIDENCE.md` that posted messages are "what interactive Scenes consume" was an untested assumption and has been corrected.
- Wallpaper Engine most likely drives scene pointer state from the global cursor position or its own input hook rather than from window messages, and the private render window is placed off-screen at `-32000,-32000`.
- Candidate remedies, none yet chosen: keep the render window positioned behind the Codex window so the real cursor is genuinely over it; confirm or refute the global-cursor hypothesis with a one-off experiment that moves the real cursor; or label the native backend as non-interactive and route interactive scenes to the safe renderer.
- Until one of these is implemented and measured, the product must not promise pointer interaction for the native backend.

## ADR-007 — The injected layer cannot reach an HTTP frame stream

**Status:** accepted (measured on Windows build 26200, Codex 2026-09-20)

Issue #1 section 3 asks to avoid per-frame JPEG/Base64 transport. A loopback MJPEG server was implemented, unit-tested (loopback-only bind, per-session token, two served paths, multipart framing, latest-wins backpressure) and wired behind a page-side reachability probe that could switch transports at runtime and fall back to CDP.

Measured against a live Codex page, the probe returned `cdp:probe-refused/fetch-refused`: a Codex `app://` page refuses **both** an `<img>` load and a `fetch` to `http://127.0.0.1:<port>`, including a same-machine loopback with a per-session token. An HTTP frame stream is therefore unreachable from the injected layer, and no `blob`/`data` alternative changes that, because the channel itself is blocked rather than the encoding.

Decision: the MJPEG server and its wiring were removed rather than shipped as network code that can never run on the supported platform. Consequences for the transport axis:

- the CDP channel remains the only transport, so the injected presentation layer still performs no script-initiated network access of its own, and the injected runtime keeps its "no `fetch`, no `XMLHttpRequest`" property, which the self-tests assert;
- transport cost must be attacked by sending **smaller or partial payloads over CDP** (for example WebP at equal quality, or changed-region updates) rather than by changing the channel;
- if a future Codex build allows loopback requests from `app://`, the reachability-probe design recorded in this decision is the pattern to reintroduce, and it must stay feature-detected rather than assumed.
