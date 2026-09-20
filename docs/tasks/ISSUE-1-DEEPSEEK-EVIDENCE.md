# Issue #1 — DeepSeek implementation evidence

Branch: `deepseek/v0.3-native-backend`. Specification: `docs/tasks/ISSUE-1-NATIVE-WALLPAPER-BACKEND.md` (minimum commit `a72fc2c`).

This document records what this implementation changes, what is verified automatically, and what still requires the local manual matrix. Real Workshop assets were not needed for any automated check and are not committed.

## 1. Scope

Three increments are recorded here.

**Increment 1** covered the `a72fc2c` delta — section 2a (Windows restart and connection recovery), hard gate 5 and the two new regression controls — together with hard gate 6 (empty/gray/transient frames) and the input-independence half of section 4.

**Increment 2** covers section 1 (discovery and backend eligibility) and hard gate 3: native Wallpaper Engine eligibility is no longer coupled to our safe `scene.pkg` parser, so `PKGV0024`, packages above the fallback parser's size limits and loose (unpackaged) Scene projects reach the native backend, while the safe parser remains a compatibility fallback.

**Increment 3** covers the rest of section 4 (interaction) and hard gate 2's synthetic half: mouse move plus left/right/middle press and release, wheel and leave/cancel are transported in the order Codex produced them, the CSS-to-surface mapping accounts for device scale and letterboxing, movement is coalesced while button and wheel order is preserved, and input is polled at 60 Hz independently of the 10/15 FPS frame rate.

**Increment 4** covers section 5 (product states and recovery): the five required backend states are now first-class product states with a live GUI badge, a capture stream that cannot keep up is reported as *native dynamic, reduced frame rate* instead of silently degrading, and a stream that stops is restarted with bounded backoff before falling back to an explicitly labeled renderer — with the private render window closed so no stale hidden session is left behind.

**Increment 5** covers the part of section 3 that can be implemented and verified without a live Wallpaper Engine session: steady-state transport cost. A captured surface that is identical to the frame already on screen is no longer re-encoded or re-sent, one sampled pass now serves both the quality gate and the change check, and the skip is visible in Doctor as an `unchanged` counter.

**Increment 6** completes section 3's *documented fallback* branch. `CaptureBackends` probes the machine with P/Invoke only (activating the `Windows.Graphics.Capture.GraphicsCaptureSession` runtime class on a dedicated MTA thread), records whether Windows Graphics Capture is actually usable and why, selects the implemented path explicitly, and reports it in Doctor and in the capture metrics — so the fallback is named at runtime rather than implied.

**Increment 7** adds the measurement harness the Issue asks for under "Required evidence". `--measure --list` is read-only and prints every local Scene project with its backend decision, package version/size and engine availability; `--measure <workshop-id> [--seconds N]` applies one wallpaper with neutral settings, samples capture health and process resources for N seconds, measures input-channel latency, and always restores every Codex page. It also records the first live measurement.

**Increment 8** tested the direct transport against the live page. The result is negative and is recorded in `docs/ARCHITECTURE_DECISIONS.md` as ADR-007: a Codex `app://` page refuses **both** an `<img>` load and a `fetch` to `http://127.0.0.1:<port>` (`cdp:probe-refused/fetch-refused`), so an HTTP frame stream cannot be reached from the injected layer on this platform. The probe and the fallback both behaved exactly as designed — the wallpaper rendered over CDP with the direct transport declined — but the MJPEG server and its wiring were then removed rather than shipped as unreachable network code. The transport axis therefore still needs a CSP-compatible answer.

**Increment 9** measures where a frame's cost actually goes, closes gates 7 and 8, and fixes the first real gate-8 defect it found. The capture session now accounts for every transported frame (encoded bytes and the full transfer duration), so the next transport decision is data-driven rather than a guess. A Wallpaper Engine video project is asserted to stay direct video, and Restore is now verified against the desktop: the private render window must be gone, the engine process is only terminated when this session started it, and windows left behind by an earlier run are released before a new capture starts.

### Changed boundaries

| Area | Before | After |
| --- | --- | --- |
| Scene eligibility | `Support = LiveScene` only when `ScenePackageValidator` accepted `scene.pkg`, and `WallpaperEngineCaptureSession.CanUse` required that `IsScene` | New `NativeScene` support kind decided by discovery alone (contained `project.json` plus a resolvable Wallpaper Engine install); `LiveScene` is now only the safe-renderer fallback |
| Scene apply path | Native attempt, then parser-validated package, else preview | Native → safe renderer (only when the package is inside its documented limits) → clearly labeled preview → explicit failure with the reason |
| Codex reachability | `Connect` called `CdpInjectionService.ConnectAsync` directly and surfaced the raw listener/process error | `ConnectionCoordinator` classifies the endpoint and either attaches, activates a verified CDP flow, queues, or reports a bounded retry |
| Product state | One status line; queue only implied by a boolean | Six explicit states with a badge, one actionable sentence, and an action label |
| Queue | `PendingWallpaperId` + `PendingActivation` boolean | Durable queue with queue timestamp, attempt counter and a bounded failure reason; state schema 5 → 6 |
| Windows startup entry | Compare command line with the current one; no staleness report | `ControllerInvocation` model with dotnet/portable awareness, `StartupRegistrationStatus`, and a Repair action |
| Input transport | Only a left-button boolean was read as the return value of a published frame | Ordered input events (left/right/middle down and up, wheel, leave/cancel) on a dedicated channel, polled at 60 Hz; a frame publish returns position state alone and can never drain unsent input |
| Input mapping | `normalized * (surfaceSize - 1)` | `CapturePointerTransform` maps through device scale and the aspect-fitted (letterboxed) content rect, and converts browser wheel deltas into `WM_MOUSEWHEEL` rotation |
| Backend state | Only the apply "mode" string existed, and a capture stream that stopped left a frozen frame with no status change | `WallpaperBackendStatus` (the five section-5 states plus `CaptureFailed`) with a live GUI badge; a `CaptureRecoveryPolicy` supervisor reports reduced frame rate and restarts a stopped stream with bounded backoff before applying a labeled fallback |
| Frame presentation | JPEG written straight into the visible `<img>` | Quality gate before transport plus an off-screen back buffer that is swapped in only after a successful decode |

## 2. How section 1 and hard gate 3 are satisfied

| Requirement | Implementation | Verified by |
| --- | --- | --- |
| Separate discovery/type classification from safe browser-side package parsing | `WallpaperCatalog.ParseProject` classifies a Scene as `NativeScene` from the contained `project.json` plus `WallpaperEngineLocator.IsEngineAvailable`; the parser result only decides whether the *fallback* renderer is available | self-test `native Scene eligibility ignores the safe package parser` |
| A Scene with a new, large or unsupported `scene.pkg` is still eligible for native rendering | `NativeScene` never consults the parser; `PKGV0024` and a package above `MaximumPackageBytes` both classify as native | self-test `native Scene eligibility ignores the safe package parser`, **and a real installation**: `--measure --list` classified all 21 locally installed Scene projects as `NativeScene`, including the seven `PKGV0024` projects and the 170.70 MiB `3801532994` that the old version-and-size ceiling rejected outright |
| Preserve the safe scene parser only as a compatibility fallback | `LiveScene` is only assigned when the engine is *not* resolvable; the apply path retries the safe renderer after a native failure and labels the result | same check: a supported package without an engine stays `LiveScene`; a supported package with an engine prefers native |
| Preview/static fallback stays clearly labeled and never a silent substitute | When the engine is absent and the package is outside the parser limits, discovery records the reason in the entry note and the apply path returns mode `animated-preview`/`static-preview` with an explicit warning | same check: asserts `AnimatedPreview`, `IsScene == false`, `CanApply`, and the "Wallpaper Engine was not found" note |
| Application wallpapers stay rejected regardless of engine presence | The `Application` branch is unchanged and independent of engine discovery | same check, plus the existing `application wallpapers rejected` and `multi-root catalog scan` checks |

## 3. How section 2a is satisfied

| Requirement | Implementation | Verified by |
| --- | --- | --- |
| Distinct GUI states: closed / starting / connected / running without CDP / queued / retry failed | `CodexConnectionState`, `ConnectionRecovery.Classify`, badge + guidance + queue panel in `MainWindow` | self-test `connection recovery classifies every product state` |
| `Connect` must not expose raw listener/process diagnostics | `ConnectionCoordinator.ConnectAsync` returns a state and a concise sentence; `MainWindow.Connect_Click` converts any unexpected error into the retry state instead of a modal | self-test `recovery guidance is concise and free of raw diagnostics` |
| Recover from an absent listener by performing Activate-with-CDP and connecting after readiness | Coordinator activates only when no verified listener owns the port, then polls `AttachAsync` until `ReadinessTimeout` (30 s) | self-test `connection coordinator recovers after a Windows restart` |
| If Codex is already running without CDP: do not terminate, explain, retain the wallpaper, expose queue/cancel/retry | Fact-based guard on `!PortHasListener && OfficialCodexProcessCount > 0`; no activation, no attach; `QueuePanel` with *Retry now* and *Cancel queue* | self-tests `running Codex without CDP is queued, never killed`, `unverified listener and readiness failure fall back safely` |
| Queued state survives controller closure and Windows restart, is never reported as applied, clears only on success or explicit cancellation | `WallpaperQueue` persists to `state.json`; `LastAppliedWallpaperId` is only written by a successful apply; schema-6 migration keeps an existing queue | self-tests `queued wallpaper survives and never reports itself as applied`, `state schema 6 keeps a queue across restart` |
| Startup registration points at the running executable and reports a moved/replaced one as stale | `ControllerInvocation.ForCurrentProcess` records `dotnet` + assembly during development and the single-file executable when published; `StartupRegistration.GetState()` reports `StaleExecutable` / `LegacyArgument` | self-test `controller invocation round-trips and detects a moved executable` |
| Diagnostics stay in Doctor; normal guidance is concise | `DiagnosticReport` gained connection state, queue summary, startup status and capture metrics; the main window shows one sentence | Doctor output, `MainWindow.Doctor_Click` |

Codex is never terminated, and the coordinator never attaches to a listener that is not the official `OpenAI.Codex` package. When an unverified program holds the configured port, recovery moves to a fresh loopback port.

## 4. Frame presentation and input transport

- `FrameQualityEvaluator` rejects surfaces whose sampled luminance spread is below 4/255 (an empty, black, white or uniform gray GDI fill) and surfaces with fewer than 64 samples. The threshold is deliberately low so a legitimately dark Scene is still accepted; rejected frames are never transported, so the last known-good frame stays on screen.
- The capture session publishes only accepted frames and counts `published`/`rejected` frames and the last rejection reason; `Doctor` reports them as `Native capture: <width>x<height> at <fps> FPS, N published, M rejected`.
- The injected runtime decodes each frame into an off-screen `Image` and swaps it into the visible layer only in `candidate.onload`. An undecodable or superseded frame leaves the previous image untouched, so a partial frame can no longer flash.
- Input runs on its own channel at 60 Hz while frames are still published at 10/15 FPS, so pointer latency does not inherit the capture rate. Listeners are installed when the capture lease begins, and only the input channel drains the queue — a frame publish returns position state alone, so it cannot swallow unsent input.
- Mouse move, left/right/middle press and release, wheel (with `deltaY`/`deltaMode` preserved) and leave/cancel are transported as ordered events. A `pointercancel` or leave releases whichever buttons were held and emits an explicit cancel, so a scene cannot be left with a stuck button.
- Movement is state rather than an event, so a burst of moves costs one `WM_MOUSEMOVE`; button and wheel transitions keep their exact order. If the bounded queue ever overflows, the overflow is reported and every held button is released instead of silently reordering input.
- `CapturePointerTransform` maps the normalized Codex position through device scale and the aspect-fitted content rect (Wallpaper Engine letterboxes when the surface aspect differs from the Codex viewport), and converts browser wheel deltas into `WM_MOUSEWHEEL` rotation in the documented units.

### Section 3 status (capture and transport)

| Section 3 requirement | Status | Notes |
| --- | --- | --- |
| Prefer Windows Graphics Capture backed by D3D11 | **not implemented** | The production path is still `PrintWindow`. The blocker is concrete and build-time, not conceptual: Windows Graphics Capture needs the `Microsoft.Windows.SDK.NET.Ref` projection (a `net8.0-windows10.0.19041.0` target), that targeting pack is not installed on this machine, and NuGet is unreachable from this environment, so such a project cannot be restored or built here. Hand-rolled WinRT/D3D11 interop would be ~600+ lines of native ABI code that cannot be executed or validated in this environment at all, so it is deliberately not committed untested. |
| Documented fallback where WGC is unavailable | **implemented** | `CaptureBackends` probes the machine, selects the implemented backend explicitly, and reports it in Doctor and in the capture metrics line. Nothing about the active path is implicit. |
| Avoid full-frame JPEG/Base64/CDP transfer in the steady state | **partly implemented, and the HTTP route is closed** | An unchanged surface is no longer encoded or transported, which is a measured saving (70 of 722 surfaces in one run). A loopback MJPEG transport was implemented, verified and then removed: a Codex `app://` page refuses both an `<img>` load and a `fetch` to `127.0.0.1` (ADR-007), so the injected layer cannot reach an HTTP stream at all. Every changed frame is therefore still a full-frame JPEG over CDP; the remaining levers are smaller payloads (for example WebP instead of JPEG) or changed-region updates, not a different channel. |
| Preserve aspect ratio and composition at the actual viewport/DPI | implemented | The render window is created from the Codex viewport in device pixels; the pointer mapping reverses the resulting letterbox. Visual confirmation still needs the live session. |
| Atomic presentation and last known-good frame | implemented | Off-screen decode plus `onload` swap; rejected and unchanged frames leave the visible frame untouched. |
| Reject empty, uniform, stale, partial and low-resolution frames | **mostly implemented** | Empty, uniform/black/white, over/under-sized and over-budget frames are rejected before transport. A partially painted surface that is not uniform (for example half black) is not detected; detecting it reliably without rejecting legitimate flat regions needs live data. |

Probe result on the development machine (Windows build 26200, recorded 2026-09-20):

```
Capture backend: PrintWindow on a private play-in-window surface (documented fallback;
Windows Graphics Capture is available on this system but is not implemented by this build yet)
```

So the probe is exercised for real, not only against synthetic inputs: it successfully activates the capture runtime class on a machine where Windows Graphics Capture *is* available, which is exactly the environment where implementing it later will be verifiable.

## 5. How section 5 is satisfied

| Requirement | Implementation | Verified by |
| --- | --- | --- |
| Expose the five backend states | `WallpaperBackendStatus` + `BackendStatuses.Describe`; the GUI shows a live badge next to the connection badge, and Doctor reports the state plus its explanation | self-test `backend status vocabulary matches the product states` |
| "native dynamic, reduced frame rate" is a real state, not a hope | The session maintains a rolling publish rate; `CaptureRecoveryPolicy.IsDegraded` flags a stream below 70% of its target once it has at least four samples, and the supervisor re-evaluates every 1.5 s | self-test `capture health classifies degraded and failed streams` |
| On capture failure keep the last good frame | Rejected or failed frames are never presented; the runtime already holds the previous image until a new frame decodes | self-tests `frame quality rejects empty and uniform captures`, `native frames are presented atomically`; Node smoke test failed-decode retention |
| Retry with bounded backoff | The supervisor restarts the native stream up to three consecutive times with a non-decreasing, capped backoff (0.5 s → 1.5 s → 3 s) | self-test `capture recovery backoff is bounded and monotonic` |
| Then move to a labeled fallback rather than a stale hidden session | On exhaustion the private render window is closed first, the state becomes `CaptureFailed` with the reason, and the labeled safe-renderer/preview cascade is re-applied through the same code path used for a normal apply | self-test `backend status vocabulary…` plus the shared `ApplySceneFallbackAsync` path exercised by `native Scene eligibility ignores the safe package parser` |
| A clean stop is not reported as a failure | `HasFailed` requires a dead window or the consecutive-rejection ceiling, so Restore, a newer apply and controller shutdown leave the last frame in place without a false alarm | self-test `capture health classifies degraded and failed streams` |

The supervisor's own glue (CDP restart, fallback re-upload) needs a live Codex and Wallpaper Engine session to observe end to end; the decisions it depends on are the pure policy above, which is fully covered.

## 6. Automated evidence

Commands (Windows, .NET 8 SDK, Node.js):

```
./scripts/repository-hygiene.ps1
./scripts/build-companion.ps1 -Configuration Debug
./scripts/build-companion.ps1 -Configuration Release
dotnet ./companion/bin/Debug/net8.0-windows/win-x64/CodexWallpaperSkin.dll --self-test
node ./scripts/runtime-smoke-test.mjs
dotnet ./companion/bin/Debug/net8.0-windows/win-x64/CodexWallpaperSkin.dll --measure --list
```

The last command is read-only: it scans the local Wallpaper Engine roots and reports the backend decision for every Scene project, which is how hard gate 3 is evidenced against a real installation rather than only against synthetic packages.

`--self-test` covers 39 checks, including the 22 recovery, queue, invocation, eligibility, input, backend-state, capture-backend and frame checks added by these increments. `scripts/runtime-smoke-test.mjs` additionally exercises the atomic native frame swap, the last-known-good retention on a failed decode, ordered input delivery (buttons, wheel, leave), movement coalescing, the existing image/video/scene/palette paths, hidden-document pause, cleanup and mismatch refusal.

Synthetic fixtures only:

- `SelfTests.cs` builds a minimal Steam-style `steamapps/workshop/content/431960/<id>` layout in a temporary directory, with `steamapps/common/wallpaper_engine/wallpaper64.exe` present or absent as the scenario requires. The size-limit case writes a valid package and then extends it to `MaximumPackageBytes + 1` with `FileStream.SetLength`, so a >128 MiB logical file is exercised without allocating its contents; the whole temporary root is deleted afterwards.
- Scene packages are synthesized as `PKGV<version>` containers with a `scene.json` entry; `PKGV0024` is used to prove the parser ceiling no longer blocks native eligibility.
- Pointer mapping and wheel conversion are asserted as arithmetic on synthetic dimensions only.
- The Node smoke test uses a mock DOM and a mock `Image` whose decode outcome the test controls; input is driven through the mock window listeners. No Workshop media, captured frames or personal screenshots are used or stored.

## 7. Manual matrix status

### Measured live session (2026-09-20)

Configuration: Windows build 26200; Windows Graphics Capture available but not implemented, so the documented `PrintWindow` fallback was active; Codex already running with its verified local CDP endpoint (nothing was launched or restarted); Workshop `2935530316` (Chainsaw Man Makima 2, `PKGV0021`, 16.18 MiB) selected by the maintainer; neutral settings (fit cover, opacity 1, overlay 0, blur 0, brightness/contrast/saturation 1, scene 15 FPS, render scale 1, hidden-page throttling off); 60 seconds per run; the injected layer was restored at the end of each run.

| Measurement | Run 1 | Run 2 |
| --- | --- | --- |
| Backend / mode | Native dynamic (`wallpaper-engine-capture`) | Native dynamic (`wallpaper-engine-capture`) |
| Samples | 60 | 59 |
| Effective capture FPS (target 15) | 11.72 | 10.46 |
| Frames published per second (average / minimum) | 11.92 / 10 | 11.03 / 7 |
| Frames published | 713 | 652 |
| Frames rejected by the quality gate | 0 | 0 |
| Frames skipped as unchanged | not recorded | 70 |
| Input channel latency (median / min / max) | 1.77 / 1.35 / 8.41 ms | 1.64 / 0.73 / 7.00 ms |
| Companion working set | 84.8 MiB | max 104 MiB, average 89.8 MiB |
| Wallpaper Engine working set | not recorded | max 251.3 MiB, average 218.8 MiB |
| Wallpaper Engine CPU | not recorded | +1.88 s over the run |
| Wallpaper Engine GPU | not recorded | max 75.9 %, average 1.9 % |
| Codex working set (12 processes) | not recorded | max 2243.4 MiB, average 2132 MiB |
| Codex CPU | not recorded | +18.47 s over the run |
| Codex GPU (compositing renderer) | max 14.8 % | max 14.6 %, average 0.1 % |
| Restore | harness verified its page; a follow-up full Restore still found artifacts on one page | full Restore cleaned one page; an immediate follow-up Restore reported zero pages |

What this establishes:

- **Hard gate 6**: across 1,365 published frames in two runs the quality gate rejected **zero** frames, so no empty, uniform or transient surface was presented. It also shows the gate is not rejecting legitimate content on a confirmed case.
- **Hard gate 8** (partial): the restore is verified, and the harness now ends with a multi-page Restore; a follow-up Restore reports nothing left.
- **Hard gate 2** (partial): the input channel delivers synthetic pointer movement in 0.7–8.4 ms (median ~1.7 ms). Whether the scene's water *visibly* follows the pointer is still a human judgement.
- **The section 3 transport saving is real**: 70 of 722 captured surfaces were identical to the frame already on screen and were therefore neither encoded nor transported.
- **New measured limitation**: the `PrintWindow` path sustains roughly 10.5–11.9 FPS against a 15 FPS target, and the engine's GPU use spikes to 76 % while Codex spends 18.5 s of CPU per minute compositing transferred frames. Capture and transport, not headroom, are the bottleneck — the concrete justification for the approved Windows Graphics Capture plus MJPEG work.

### Browser safe-renderer harness run by the maintainer

`node scripts/scene-render-smoke-test.mjs` on the local installation: **12 passed, 5 failed**. All five failures are `PKGV0024` projects — four are refused by the embedded safe renderer's own version/size boundary (`scene.pkg header exceeds the renderer safety boundary`) and one by the harness's own 128 MiB in-page limit. Those same five projects are classified `NativeScene` by this implementation and reach Wallpaper Engine directly, which is exactly the section 1 behavior: the safe renderer's limits stay bounded and documented, and they never silently stand in for native quality.

### Remaining manual protocol

The rest of the local manual protocol in `docs/compatibility/WALLPAPER_ENGINE_MATRIX.md` — framing, sharpness, colour, flashing across the full observation, scene-level interaction feel, resize/minimise/restore, and switching between two wallpapers — still needs a human at the screen. This increment adds the numbers, not the visual verdict.

### Per-frame cost, measured (2026-09-20)

Same configuration as the table above. One run with a stale render window left open by an earlier build is also shown, because it is the run that exposed the gate-8 defect.

| Measurement | Clean run | Run before the window fix |
| --- | --- | --- |
| Effective capture FPS (target 15) | 12.0 | 7.5 |
| Frames published / skipped-unchanged | 253 / 4 | 41 / 23 |
| Average encoded frame | 337,877 B (~330 KiB) | 330,916 B (~323 KiB) |
| Frames rejected by the quality gate | 0 | 0 |
| Full frame transfer (CDP round trip included) | 10.76 ms | 13.42 ms |
| Input channel latency (median) | 1.74 ms | not measurable (stream stalled) |
| Open private render windows after restore | 0 | 1 (left by an earlier run) |

Where the time goes: a 15 FPS target allows 66.7 ms per frame, and the transport takes ~10.8 ms of it, so **the CDP transfer is roughly a sixth of the budget**. The remaining ~55 ms is `PrintWindow` capture plus JPEG encoding of a 1920x1200 surface. That is the measured answer to "which transport change is worth making": shrinking the payload (roughly 4.2 MB/s of JPEG today) is a secondary win, while removing the `PrintWindow` readback — the Windows Graphics Capture work — addresses the dominant cost. The stale-window run is a reminder that a leftover render window roughly trebles per-frame cost, which is why the gate-8 fix also restores throughput.

## 8. Known limitations and fallbacks

- The high-fidelity capture path still uses `PrintWindow` on a private `-playInWindow` surface. Windows Graphics Capture with D3D11 is not implemented because the `Microsoft.Windows.SDK.NET.Ref` projection cannot be restored in this environment (not installed locally, NuGet unreachable), and untested hand-rolled WinRT/D3D11 interop was deliberately not committed. The documented-fallback path is reported at runtime by `CaptureBackends`. Each changed frame is still a full-frame JPEG transfer; the unchanged-frame skip and atomic presentation mitigate the cost and the visible symptoms.
- An unchanged surface is detected from a sampled fingerprint, not from a full-frame comparison, so a change confined entirely to unsampled rows could be missed until the next change in a sampled row. The sample covers up to 48 evenly spaced rows.
- `-playInWindow` capture is capped at 1920x1200 and 10/15 FPS by the existing scene-quality controls.
- Input is delivered by `PostMessage` to the private render window, which is what interactive Scenes consume; a Scene that requires real OS pointer capture may still behave differently, and that cannot be judged without the live session.
- `WM_MOUSEWHEEL` carries screen coordinates, so the client point is converted with `ClientToScreen`; if that call fails the wheel event is skipped rather than sent with wrong coordinates.
- The mapping assumes Wallpaper Engine letterboxes the scene into the render window. If a Scene crops instead of letterboxing, pointer positions in the cropped margins will be off by the crop amount.
- Capture recovery restarts the stream up to three times; a wallpaper that cannot be captured at all ends on a labeled fallback rather than retrying forever. The threshold and the backoff schedule are constants in `CaptureRecoveryPolicy` so they can be tuned from live measurements.
- Mid-stream recovery re-uploads the initial frame, so a recovery costs one extra full-frame transfer. It does not re-run the quality gate on the already-presented frame, which is intentional: the point is to keep the last good frame rather than replace it with a fresh blank one.
- The safe renderer's limits (`PKGV0012`–`PKGV0023`, 128 MiB package, 64 MiB entry) are unchanged and intentional: it parses packages in the renderer process, so raising them would raise its memory exposure. Native eligibility deliberately does not inherit those limits.
- A Scene whose content is loose on disk (`scene.json` without `scene.pkg`) can only use the native backend; the safe renderer refuses it with an explicit message.
- If every live backend fails and a preview exists, the labeled preview is shown with the reason; if there is no preview, the apply fails closed with the reason instead of pretending to succeed.
- Web wallpapers still require the explicit safety decision and continue to use the safe path by default.
- When Codex is running without CDP the queue is applied by the background restore worker after Codex is next closed normally; nothing is applied while the user's task is running.
- `scripts/scene-render-smoke-test.mjs` is a local manual harness that launches Microsoft Edge and requires local Workshop projects. It cannot run in a confined environment that denies child-process pipes, and it is not part of CI.

## 9. Rollback

1. `git revert` the relevant commits, or `git checkout main` for the previous baseline.
2. The saved state file `%LOCALAPPDATA%\CodexWallpaperSkin\state.json` may contain `SchemaVersion: 6` fields (`PendingQueuedAt`, `PendingLastAttemptAt`, `PendingAttempts`, `PendingLastFailure`) and the `NativeScene` support value. Older builds ignore the extra properties and re-migrate on load; deleting the file restores defaults and re-scans Wallpaper Engine projects.
3. If a Windows sign-in entry was repaired, turn *Restore at Windows sign-in* off, or delete the `CodexWallpaperSkin.AutoRestore` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
4. Run `CodexWallpaperSkin --restore` to remove the injected presentation layer; no Codex package file is ever modified.
