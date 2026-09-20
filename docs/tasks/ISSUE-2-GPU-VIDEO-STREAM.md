# Issue #2 — High-performance GPU video stream

## Product outcome

Deliver Wallpaper Engine's native dynamic output inside Codex with desktop-like clarity, smoothness and long-running reliability. Replace the steady-state full-frame JPEG/Base64/CDP path with a bounded local media path that keeps capture, encode, decode and composition on the GPU wherever the platform permits.

This is one shared specification for `codex/v0.4-gpu-stream` and `deepseek/v0.4-gpu-stream`. Both branches start from the same `main` commit and are independent implementation sources. The objective is the best final product, not a model ranking. Final convergence may adopt either design, combine verified components, or reject both.

## Baseline and problem statement

The v0.3 native backend correctly delegates complex Scene rendering to Wallpaper Engine and prefers Windows Graphics Capture, but each accepted frame is still JPEG-encoded, Base64-expanded and delivered through serialized CDP evaluations. That path is CPU-heavy, bandwidth-inefficient and intentionally capped at 10 or 15 FPS. It also makes decode acknowledgement, stale-session isolation and long-running recovery unnecessarily difficult.

The v0.4 production path must remove per-frame JPEG, Base64 and CDP evaluation from steady-state dynamic playback. CDP may install and control a persistent presentation surface, but it must not carry normal video frames.

## Required architecture boundaries

### 1. Capture and GPU ownership

- Continue to let Wallpaper Engine render supported Scene projects through its documented `playInWindow` lifecycle.
- Prefer Windows Graphics Capture with D3D11-backed frames.
- Keep the private render window composed and capturable without exposing it in the taskbar or Alt+Tab, minimizing it, taking focus or leaving orphaned windows.
- Keep one immutable stream/session identity from capture through presentation. A late frame, callback or worker from an old session must never enter a replacement stream.
- Bound frame queues. Under load, drop obsolete frames instead of accumulating latency or memory.

### 2. Production media path

- Target a hardware-accelerated path such as D3D11 capture to Media Foundation hardware H.264 encoding, a bounded local transport, and hardware decode/composition in one persistent Codex video surface.
- An alternative implementation is acceptable only when measured evidence shows equivalent or better quality, latency, resource use, lifecycle safety and Windows availability.
- Prefer Windows-provided media components. New redistributable codecs or native dependencies require explicit license, architecture, packaging, security and maintenance review.
- Reuse one long-lived browser presentation surface. Do not recreate an image/video DOM tree for every frame.
- Keep media transport local to the machine, authenticated or capability-scoped, loopback-only where networking is used, and closed on Restore or session replacement.
- Do not modify the installed Codex package or depend on undocumented binary patching/native injection.

### 3. Compatibility backend

- Retain the existing WGC/JPEG/CDP implementation only as an explicitly labeled reduced-frame-rate compatibility backend.
- Hardware encode/decode initialization failure must be reported clearly and must select a safe fallback rather than silently claiming full performance.
- A 30 FPS mode may be selected for high resolution or constrained hardware. Below 30 FPS must be labeled degraded and may reduce capture scale after user-visible diagnosis.
- Static image, ordinary video and safe fallback behavior must not regress.

### 4. Presentation reliability

- Keep the last confirmed good frame visible during bounded capture, encode, transport or decode interruptions.
- A frame is presented only for the current immutable stream identity.
- Detect and reject empty, uniform black/gray, stale, implausibly low-resolution and incomplete frames without flashing them.
- Recovery must use bounded retry/backoff and expose its current state. It must not spawn competing workers or loop forever.
- Wallpaper changes must be transactional: prepare the replacement stream, confirm a newly presented frame, atomically promote it, then retire the previous stream.

### 5. Diagnostics and product status

Doctor and the normal status surface must distinguish at least:

- GPU dynamic — 60 FPS target;
- GPU dynamic — 30 FPS fallback;
- reduced-frame-rate JPEG compatibility;
- static/preview fallback;
- recovering;
- unsupported or failed.

Record bounded, privacy-safe diagnostics for capture resolution, requested and observed FPS, encoded and dropped frames, queue depth, approximate encode/presentation latency, encoder/decoder mode, restart/recovery count, current stream identity and last successful presentation time. Never include captured pixels, Workshop assets, credentials or unnecessary absolute personal paths.

### 6. Existing product behavior that must remain

- Wallpaper scanning and safe type classification.
- Search, custom names, personal collections and type filters.
- Fit/focus/color/readability controls.
- queued apply and safe normal Codex restart recovery.
- startup restore, explicit Restore and reversible injection.
- application-wallpaper rejection and current path-containment boundaries.

## Performance intent

- 60 FPS is the primary product target on capable hardware with a 60 FPS source at a 1920×1080 capture target.
- 30 FPS is an explicit fallback for constrained devices or higher resolutions, not the default success claim.
- A source-authored lower frame rate is not fabricated into 60 unique frames; status must distinguish source cadence from transport/display capacity.
- Quality must not be achieved by silently stretching a Workshop preview or reducing resolution below the reported capture scale.

Exact gates, workloads and evidence are defined in `docs/acceptance/V0.4-GPU-STREAM-ACCEPTANCE.md`.

## Non-goals

- Reimplementing Wallpaper Engine Scene shaders or effects.
- Reproducing general mouse-interactive wallpaper behavior in Codex. Pointer forwarding from v0.3 may remain disabled or compatibility-only, but it must not block or destabilize playback.
- Per-wallpaper title, Workshop-ID, texture or shader patches.
- Executing Wallpaper Engine Application projects.
- Purchasing a signing certificate or publishing a GitHub Release.
- Selecting a winning model. Evaluation is component- and product-based.

## Required deliverables from each branch

1. Architecture note describing capture, encoder, transport, decoder/presenter, session ownership, fallback and cleanup.
2. Source implementation with bounded queues and cancellation.
3. Automated lifecycle, stale-stream, fallback and diagnostics tests using synthetic or redistributable fixtures only.
4. A separately named portable package and SHA-256 file.
5. Completed acceptance report using the shared matrix and the same machine/settings.
6. Known limitations, dependency/license inventory, rollback instructions and changes recommended for final convergence.

## Safety and repository rules

- Never commit installed Workshop media, captured wallpaper frames, personal screenshots, local state, logs containing personal paths, credentials, API keys, build outputs or caches.
- Do not force-close Codex or Wallpaper Engine during automated tests unless a dedicated synthetic fixture explicitly owns the process.
- Do not merge either implementation branch into `main` before product-level convergence and maintainer approval.
- Every GitHub Release remains a draft until the maintainer publishes it manually.
