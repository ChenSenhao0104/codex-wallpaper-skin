# Wallpaper Engine compatibility matrix

This matrix records user-reported product cases for Issue #1. It contains metadata only. Installed Workshop files remain local and are not copied into Git, CI artifacts, Issues or releases.

## Manual test protocol

Use the same Codex window size, Windows display scale and settings for both implementation branches. For every case:

1. Apply the wallpaper and wait 60 seconds.
2. Observe framing, sharpness, color and any black/gray/partial-frame flashes.
3. Move the pointer over all major regions, then test left/right/middle press and release plus wheel.
4. Resize/minimize/restore Codex and switch away and back.
5. Switch to a second wallpaper and return to the first.
6. Run Restore and confirm that the complete layer and owned resources disappear.

Record `pass`, `fail`, `fallback` or `not applicable`, plus the active backend and evidence. Do not attach Workshop assets or personal screenshots to the repository.

## Confirmed installed projects

| Workshop ID | Project | Type / package evidence | Expected product behavior | Previously observed problem |
| --- | --- | --- | --- | --- |
| [2935530316](https://steamcommunity.com/sharedfiles/filedetails/?id=2935530316) | Chainsaw Man Makima 2 | Scene, PKGV0021, 16.18 MiB | Native dynamic, correct full composition, stable frames | Enlarged/blurred before native path; later periodic flashing |
| [3494484288](https://steamcommunity.com/sharedfiles/filedetails/?id=3494484288) | Pastel Bloom - Anime Girl Aesthetic | Scene, PKGV0022, 22.42 MiB | Native dynamic with intended person, ring and color | Preview/fallback showed a large ring, wrong framing and color |
| [2914257158](https://steamcommunity.com/sharedfiles/filedetails/?id=2914257158) | Saki Tenma — Abyss of Memories | Scene, PKGV0018, 10.69 MiB | Native dynamic; water follows pointer; no flashing | Browser fallback lost interaction; prototype later flashed |
| [3798584436](https://steamcommunity.com/sharedfiles/filedetails/?id=3798584436) | Lofi Girl — Summer Festival | Scene, PKGV0024, 10.24 MiB | Native dynamic | Rejected by old PKGV0023 ceiling; enlarged animated preview |
| [3801532994](https://steamcommunity.com/sharedfiles/filedetails/?id=3801532994) | 爱弥斯窗外雨天【dy安静】 | Scene, PKGV0024, 170.70 MiB | Native dynamic | Rejected by version and 128 MiB/64 MiB parser limits; preview fallback |
| [3800850100](https://steamcommunity.com/sharedfiles/filedetails/?id=3800850100) | 钰烛 · 云影池 · 幼年期 [8K] | Scene, PKGV0024, 92.76 MiB | Native dynamic at an appropriate capture resolution | Rejected by old version ceiling; enlarged/blurred preview |
| [3799703549](https://steamcommunity.com/sharedfiles/filedetails/?id=3799703549) | alona | Scene, PKGV0024, 4.06 MiB; static preview | Native dynamic when available; clearly labeled static fallback otherwise | Rejected by old version ceiling; static fallback enlarged |
| [3803167460](https://steamcommunity.com/sharedfiles/filedetails/?id=3803167460) | Geralt of Rivia (Witcher) | Scene, PKGV0024, 54.15 MiB | Native dynamic | Rejected by old version ceiling; enlarged animated preview |
| [3799253558](https://steamcommunity.com/sharedfiles/filedetails/?id=3799253558) | Reze & Bomb Devil — Ultrawide | Scene, PKGV0024, 40.46 MiB | Native dynamic with explicit letterbox/crop behavior | Rejected by old version ceiling; preview framing/blur |
| [3796846129](https://steamcommunity.com/sharedfiles/filedetails/?id=3796846129) | Toejam & Earl | Scene, PKGV0024, 101.02 MiB | Native dynamic | Rejected by old version ceiling; enlarged animated preview |
| [3756621387](https://steamcommunity.com/sharedfiles/filedetails/?id=3756621387) | 阿洛娜指纹识别 2.0（网页版） | Web (`index.html`, JS/WASM/audio assets) | Controlled native Wallpaper Engine backend only after explicit Web safety confirmation | Current safe path deliberately uses animated preview |

## Regression controls

| Case | Expected backend/result |
| --- | --- |
| Local static image | Direct image; unchanged |
| Local MP4/WebM | Direct video; unchanged |
| Wallpaper Engine video | Direct video or documented native capture fallback |
| Valid simple Scene supported by safe parser | Native capture by default; safe renderer remains available as fallback |
| Scene unsupported by safe parser | Native capture; never silently treated as a low-resolution equivalent |
| Web wallpaper | Explicitly controlled native backend or clearly labeled preview fallback |
| Application wallpaper | Rejected |
| Capture unavailable/fails | Last-good frame, bounded retry, then clearly labeled safe fallback |
| Controller opened before Codex after Windows restart | Connect activates verified Codex CDP and waits for readiness; no raw missing-listener modal |
| Codex already open without CDP | Preserve current task, show queued/cancel/retry state, and never claim the wallpaper is already applied |

## Result table

Fill this table during product convergence; it evaluates behavior, not authors or models.

| Workshop ID / control | Codex candidate | DeepSeek candidate | Converged product | Notes/evidence |
| --- | --- | --- | --- | --- |
| 2935530316 |  | measured |  | Live session 2026-09-20, 60 s, native dynamic: 713 and 652 frames published, 0 rejected, 10.5–11.9 FPS against a 15 FPS target, input latency median 1.6–1.8 ms, restore verified. The framing/sharpness/flashing verdict still needs a human at the screen; see `docs/tasks/ISSUE-1-DEEPSEEK-EVIDENCE.md`. |
| 3494484288 |  | not run |  | Local manual protocol not executed in the automated increment. |
| 2914257158 |  | not run |  | Pointer movement, buttons and wheel are transported and covered by synthetic tests; whether the water actually follows the pointer still needs the local manual protocol. |
| 3798584436 |  | not run |  | Native eligibility for PKGV0024 is verified automatically (`--self-test`); visual fidelity still needs the local manual protocol. |
| 3801532994 |  | not run |  | Native eligibility for PKGV0024 and above the 128 MiB fallback parser limit is verified automatically; visual fidelity still needs the local manual protocol. |
| 3800850100 |  | not run |  | Native eligibility for PKGV0024 is verified automatically; visual fidelity still needs the local manual protocol. |
| 3799703549 |  | not run |  | Native eligibility for PKGV0024 is verified automatically; the labeled static fallback is unchanged when no engine is present. |
| 3803167460 |  | not run |  | Native eligibility for PKGV0024 is verified automatically; visual fidelity still needs the local manual protocol. |
| 3799253558 |  | not run |  | Native eligibility for PKGV0024 is verified automatically; letterbox/crop behavior still needs the local manual protocol. |
| 3796846129 |  | not run |  | Native eligibility for PKGV0024 is verified automatically; visual fidelity still needs the local manual protocol. |
| 3756621387 |  | not run |  | Local manual protocol not executed in the automated increment. |
| Static image control |  | pass (auto) |  | `scripts/runtime-smoke-test.mjs` image path unchanged. |
| Ordinary video control |  | pass (auto) |  | `scripts/runtime-smoke-test.mjs` video path unchanged. |
| Application rejection control |  | pass (auto) |  | `--self-test`: application projects stay rejected even with Wallpaper Engine installed. |
| PKGV0024 / over-limit / loose Scene eligibility |  | pass (auto + real install) |  | `--self-test` covers synthetic PKGV0024, over-limit and loose projects; `--measure --list` classified all 21 locally installed Scene projects as `NativeScene`, including the seven PKGV0024 projects and the 170.70 MiB one. |
| Mouse move / button / wheel / leave transport |  | pass (auto) |  | `--self-test` mapping and wheel arithmetic plus the Node smoke test's ordered-input check (right button, wheel, left button, leave) and movement coalescing. |
| Pointer mapping through device scale and letterbox |  | pass (auto) |  | `--self-test`: matching-aspect, pillar-boxed and letterboxed surface geometry, plus out-of-range clamping. |
| Product backend states (native / reduced / safe / preview / unsupported) |  | pass (auto) |  | `--self-test`: mode-to-state mapping and the five state names. |
| Native capture degraded or stopped |  | pass (auto) |  | `--self-test`: reduced-frame-rate and failed classification, bounded backoff, and the shared labeled-fallback path. Live end-to-end recovery still needs a real Wallpaper Engine session. |
| Unchanged surface is not re-transported |  | pass (auto + measured) |  | `--self-test`: fingerprint stability, invalidation on a single changed sample, prefix separation and first-frame publication. Live runs skipped 70 of 722 surfaces. |
| Loopback HTTP frame transport |  | not possible |  | Measured against a live page: a Codex `app://` page refuses both an `<img>` load and a `fetch` to `127.0.0.1` (`cdp:probe-refused/fetch-refused`), so the transport was removed. See ADR-007. |
| Capture backend is named at runtime |  | pass (auto + probe) |  | `--self-test`: selection and description for supported, unsupported and non-Windows machines; the real probe runs in `--doctor` and reports `PrintWindow … documented fallback` with the Windows Graphics Capture verdict. |
| Controller opened before Codex after Windows restart |  | pass (auto) |  | `--self-test`: coordinator activates the verified CDP flow, waits for readiness, then applies the queued wallpaper. |
| Codex already open without CDP |  | pass (auto) |  | `--self-test`: no activation, no attach, wallpaper retained as queued, `LastAppliedWallpaperId` untouched. |
| Empty/gray/transient frame |  | pass (auto) |  | `--self-test` frame-quality gate plus the Node smoke test's failed-decode last-known-good check. |
