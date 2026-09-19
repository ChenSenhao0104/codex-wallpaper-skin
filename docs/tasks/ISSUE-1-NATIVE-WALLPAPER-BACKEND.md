# Issue #1 — Native Wallpaper Engine backend

## Product outcome

Make Wallpaper Engine Scene wallpapers—including large, new-format and interactive projects—render inside Codex with their intended composition, sharpness, color and pointer behavior. Connecting must also work when Codex is already running. The normal compatibility strategy is to let Wallpaper Engine render its own project; adding per-wallpaper shader patches is not an acceptable default.

This is one shared specification for the Codex and DeepSeek implementation branches. The branches are independent implementation sources, not competitors. Final integration is component-based and may adopt either implementation, a deliberate hybrid, or neither.

## Confirmed systemic causes

1. Native capture eligibility currently depends on `WallpaperEntry.IsScene`, while `IsScene` is true only after the browser-side `ScenePackageValidator` accepts `scene.pkg`. Native Wallpaper Engine rendering does not require our package parser, so these concerns are incorrectly coupled.
2. The parser accepts only `PKGV0012`–`PKGV0023`. Several current Workshop projects use `PKGV0024` and are therefore mislabeled as preview fallbacks.
3. The parser caps packages at 128 MiB and entries at 64 MiB. A confirmed installed scene has a 170.70 MiB package/entry and is rejected before Wallpaper Engine is asked to render it.
4. Preview fallbacks are low-resolution GIF/JPEG assets. Displaying them with `cover` explains crop, enlargement and blur; it cannot reproduce real Scene behavior.
5. The first native prototype captures with `PrintWindow`, encodes every frame as JPEG, transports Base64 through CDP and immediately replaces one image source. There is no robust empty/gray/transient-frame rejection or double-buffer/last-good-frame presentation, which is consistent with the reported periodic flashes.
6. Pointer forwarding is incomplete and coupled to captured frames. It does not yet provide independent, correctly scaled move/down/up/wheel input suitable for the broad range of interactive scenes.

## Safety and privacy boundaries

- Never modify files in the installed Codex package.
- Never commit, upload or attach installed Workshop media, captured frames, personal screenshots, absolute user paths, credentials or API keys.
- Application wallpapers remain rejected.
- Web wallpapers require an explicit user-facing safety decision before native execution; network and audio implications must be clear.
- All injected state must remain fully reversible through Restore.

## Required architecture

### 1. Discovery and backend eligibility

- Separate project discovery/type classification from safe browser-side package parsing.
- A valid contained Wallpaper Engine `project.json` for a Scene may be eligible for native Wallpaper Engine rendering even when `scene.pkg` is new, large or unsupported by our fallback parser.
- Preserve the safe scene parser only as a compatibility fallback.
- Keep preview/static fallback as a clearly labeled final fallback, never as a silent substitute for native quality.

### 2. Wallpaper Engine lifecycle

- Launch or reuse a bounded `-playInWindow` Wallpaper Engine window for the selected project.
- Verify the expected process and window ownership before capture or input forwarding.
- Support switching wallpapers, cancellation, Codex reconnect, companion shutdown and Restore without orphaned windows.
- Do not require Codex to be closed before the companion starts. Diagnose and improve the existing CDP activation/attach flow independently from wallpaper rendering.

### 3. Capture and transport

- Prefer Windows Graphics Capture backed by D3D11 for the production high-fidelity path. A documented fallback may be retained for systems where WGC is unavailable.
- Avoid full-frame JPEG/Base64/CDP transfer as the steady-state architecture where practical.
- Preserve aspect ratio and source composition at the actual Codex viewport/DPI. Do not enlarge a Workshop preview to masquerade as a live scene.
- Present frames atomically (for example, double buffering) and retain the last known-good frame.
- Reject empty, uniform gray/black, obviously stale, partial and implausibly low-resolution frames without flashing them to the user.

### 4. Interaction

- Forward mouse move, left/right/middle press and release, wheel and leave/cancel semantics where supported.
- Map Codex CSS coordinates through device scale and any letterbox/crop transform into the Wallpaper Engine render surface.
- Input delivery must not wait for a successful capture frame.
- Rate-limit/coalesce movement while preserving button and wheel ordering.

### 5. Product states and recovery

Expose an understandable current backend/status:

- native dynamic;
- native dynamic, reduced frame rate;
- safe scene compatibility renderer;
- animated/static Workshop preview;
- unsupported.

On capture failure, keep the last good frame, retry with bounded backoff and move to a labeled fallback rather than flashing or leaving a stale hidden session.

## Locally verified compatibility cases

The full matrix is in `docs/compatibility/WALLPAPER_ENGINE_MATRIX.md`. Real Workshop assets are local manual fixtures only and must never enter Git. Their Workshop IDs provide reproducible references without redistributing content.

## Hard acceptance gates

1. No unintended crop, severe blur, color distortion or periodic flashes on the confirmed Scene cases.
2. Saki water interaction responds to pointer movement; common button/wheel interactions are correctly transported in synthetic tests.
3. PKGV0024 and packages larger than the safe fallback parser limits can still use the native backend.
4. Codex may already be open when the companion connects/activates CDP; no forced sign-out/restart loop.
5. Empty/gray/transient frames never replace the last known-good image.
6. Existing local image, ordinary video and direct Wallpaper Engine video behavior does not regress.
7. Restore removes the complete presentation layer and terminates owned capture resources.
8. Application wallpaper projects remain rejected.
9. Release build, self-tests, runtime smoke test and repository hygiene checks pass.
10. No private or copyrighted local test material is committed.

## Required evidence from each implementation

- Design summary and changed boundaries.
- Automated test output and synthetic fixture coverage.
- Manual matrix results using the same viewport, duration and interaction script.
- Capture resolution, average/minimum FPS, rejected-frame count, pointer latency and recovery time.
- Companion and Wallpaper Engine CPU/GPU/memory observations.
- Known limitations, fallback behavior and rollback instructions.

## Non-goals for this work package

- Reimplementing every Wallpaper Engine shader, particle system or component in JavaScript.
- One-off title/Workshop-ID checks or per-wallpaper rendering patches.
- Executing Application wallpapers.
- Publishing a GitHub Release. Any later release is drafted and requires maintainer approval.
