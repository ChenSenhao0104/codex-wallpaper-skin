# Issue #1 — DeepSeek implementation evidence

Branch: `deepseek/v0.3-native-backend`. Specification: `docs/tasks/ISSUE-1-NATIVE-WALLPAPER-BACKEND.md` (minimum commit `a72fc2c`).

This document records what this implementation changes, what is verified automatically, and what still requires the local manual matrix. Real Workshop assets were not needed for any automated check and are not committed.

## 1. Scope of this increment

The `a72fc2c` revision added section 2a (Windows restart and connection recovery), hard gate 5, and two regression controls. That delta is the primary subject of this increment, together with hard gate 6 (empty/gray/transient frames) and the input-independence rule in section 4.

### Changed boundaries

| Area | Before | After |
| --- | --- | --- |
| Codex reachability | `Connect` called `CdpInjectionService.ConnectAsync` directly and surfaced the raw listener/process error | `ConnectionCoordinator` classifies the endpoint and either attaches, activates a verified CDP flow, queues, or reports a bounded retry |
| Product state | One status line; queue only implied by a boolean | Six explicit states with a badge, one actionable sentence, and an action label |
| Queue | `PendingWallpaperId` + `PendingActivation` boolean | Durable queue with queue timestamp, attempt counter and a bounded failure reason; state schema 5 → 6 |
| Windows startup entry | Compare command line with the current one; no staleness report | `ControllerInvocation` model with dotnet/portable awareness, `StartupRegistrationStatus`, and a Repair action |
| Pointer input | Read as the return value of a published frame | Independent `__codexWallpaperSkinReadCapturePointer` channel, installed with the capture lease |
| Frame presentation | JPEG written straight into the visible `<img>` | Quality gate before transport plus an off-screen back buffer that is swapped in only after a successful decode |

## 2. How section 2a is satisfied

| Requirement | Implementation | Verified by |
| --- | --- | --- |
| Distinct GUI states: closed / starting / connected / running without CDP / queued / retry failed | `CodexConnectionState`, `ConnectionRecovery.Classify`, badge + guidance + queue panel in `MainWindow` | self-test `connection recovery classifies every product state` |
| `Connect` must not expose raw listener/process diagnostics | `ConnectionCoordinator.ConnectAsync` returns a state and a concise sentence; `MainWindow.Connect_Click` converts any unexpected error into the retry state instead of a modal | self-test `recovery guidance is concise and free of raw diagnostics` |
| Recover from an absent listener by performing Activate-with-CDP and connecting after readiness | Coordinator activates only when no verified listener owns the port, then polls `AttachAsync` until `ReadinessTimeout` (30 s) | self-test `connection coordinator recovers after a Windows restart` |
| If Codex is already running without CDP: do not terminate, explain, retain the wallpaper, expose queue/cancel/retry | Fact-based guard on `!PortHasListener && OfficialCodexProcessCount > 0`; no activation, no attach; `QueuePanel` with *Retry now* and *Cancel queue* | self-test `running Codex without CDP is queued, never killed` |
| Queued state survives controller closure and Windows restart, is never reported as applied, clears only on success or explicit cancellation | `WallpaperQueue` persists to `state.json`; `LastAppliedWallpaperId` is only written by a successful apply; schema-6 migration keeps an existing queue | self-tests `queued wallpaper survives and never reports itself as applied`, `state schema 6 keeps a queue across restart` |
| Startup registration points at the running executable and reports a moved/replaced one as stale | `ControllerInvocation.ForCurrentProcess` records `dotnet` + assembly during development and the single-file executable when published; `StartupRegistration.GetState()` reports `StaleExecutable` / `LegacyArgument` | self-test `controller invocation round-trips and detects a moved executable` |
| Diagnostics stay in Doctor; normal guidance is concise | `DiagnosticReport` gained connection state, queue summary, startup status and capture metrics; the main window shows one sentence | Doctor output, `MainWindow.Doctor_Click` |

Codex is never terminated, and the coordinator never attaches to a listener that is not the official `OpenAI.Codex` package. When an unverified program holds the configured port, recovery moves to a fresh loopback port.

## 3. Frame presentation and input independence

- `FrameQualityEvaluator` rejects surfaces whose sampled luminance spread is below 4/255 (an empty, black, white or uniform gray GDI fill) and surfaces with fewer than 64 samples. The threshold is deliberately low so a legitimately dark Scene is still accepted; rejected frames are never transported, so the last known-good frame stays on screen.
- The capture session publishes only accepted frames and counts `published`/`rejected` frames and the last rejection reason; `Doctor` reports them as `Native capture: <width>x<height> at <fps> FPS, N published, M rejected`.
- The injected runtime decodes each frame into an off-screen `Image` and swaps it into the visible layer only in `candidate.onload`. An undecodable or superseded frame leaves the previous image untouched, so a partial frame can no longer flash.
- Pointer state is read through its own CDP call each stream iteration and the listeners are installed when the capture lease begins, so interaction never waits for a frame; mouse move/down/up are forwarded with the existing device-scale mapping.

## 4. Automated evidence

Commands (Windows, .NET 8 SDK, Node.js):

```
./scripts/repository-hygiene.ps1
./scripts/build-companion.ps1 -Configuration Debug
./scripts/build-companion.ps1 -Configuration Release
dotnet ./companion/bin/Debug/net8.0-windows/win-x64/CodexWallpaperSkin.dll --self-test
node ./scripts/runtime-smoke-test.mjs
```

`--self-test` covers 30 checks, including the 13 recovery, queue, invocation and frame-quality checks added by this increment. `scripts/runtime-smoke-test.mjs` additionally exercises the atomic native frame swap, the last-known-good retention on a failed decode, the independent pointer channel, the existing image/video/scene/palette paths, hidden-document pause, cleanup and mismatch refusal.

Synthetic fixtures only: temporary PNG/GIF/PKG byte headers built in `SelfTests.cs`, scripted endpoint probes with a virtual clock, and a mock DOM/`Image` in the Node smoke test. No Workshop media, captured frames or personal screenshots are used or stored.

## 5. Manual matrix status

The local manual protocol in `docs/compatibility/WALLPAPER_ENGINE_MATRIX.md` requires the installed Workshop projects, a fixed Codex viewport and 60-second observation. It was not executed in this automated increment; the Workshop rows therefore remain `not run` for this candidate rather than being claimed as passing.

The recovery, queue, frame-quality and input-ordering behaviors that *can* be checked without those assets are covered by the automated checks above.

## 6. Known limitations and fallbacks

- The high-fidelity capture path still uses `PrintWindow` on a private `-playInWindow` surface. Windows Graphics Capture with D3D11 (section 3) is not implemented yet; the quality gate and atomic presentation mitigate the visible symptoms but not the transport cost.
- `-playInWindow` capture is capped at 1920x1200 and 10/15 FPS by the existing scene-quality controls.
- If capture cannot start or produces only rejected frames, the apply path falls back to the embedded safe Scene renderer or the clearly labeled Workshop preview, and the user-visible mode string names the fallback.
- Web wallpapers still require the explicit safety decision and continue to use the safe path by default.
- When Codex is running without CDP the queue is applied by the background restore worker after Codex is next closed normally; nothing is applied while the user's task is running.
- `scripts/scene-render-smoke-test.mjs` is a local manual harness that launches Microsoft Edge and requires local Workshop projects; it cannot run in a confined environment that denies child-process pipes, and it is not part of CI.

## 7. Rollback

1. `git revert` this branch's commits, or `git checkout main` for the previous baseline.
2. The saved state file `%LOCALAPPDATA%\CodexWallpaperSkin\state.json` may contain `SchemaVersion: 6` fields (`PendingQueuedAt`, `PendingLastAttemptAt`, `PendingAttempts`, `PendingLastFailure`). Older builds ignore unknown properties and re-migrate on load; deleting the file restores defaults.
3. If a Windows sign-in entry was repaired, turn *Restore at Windows sign-in* off, or delete the `CodexWallpaperSkin.AutoRestore` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
4. Run `CodexWallpaperSkin --restore` to remove the injected presentation layer; no Codex package file is ever modified.
