---
name: codex-wallpaper-skin
description: Apply, color-coordinate, tune, troubleshoot, or remove reversible local-image, local-video, and Wallpaper Engine backgrounds in the official Store/MSIX Codex desktop app on Windows 11 x64 through a loopback-only CDP companion. Use for Codex background or wallpaper requests; do not use for Codex CLI syntax themes or ordinary official appearance settings.
---

# Codex Wallpaper Skin

Use the bundled companion to place a non-interactive background layer behind the real Codex interface. The media is loaded independently inside Codex; never substitute the user's desktop wallpaper or require the same wallpaper to be running on the desktop. Keep user data and installation files unchanged.

## Route the request

- For compatibility or environment questions, run `scripts/doctor.ps1 -Json`. The GUI Doctor can export a privacy-reduced diagnostic ZIP with controller logs; it excludes wallpaper media, the raw state file, and Codex task titles. Read [references/compatibility.md](references/compatibility.md) only when interpreting the result.
- For an ordinary background change, run `scripts/launch.ps1`. The visible companion window is the normal user interface; do not make self-test, runtime-smoke-test, release, or skill-validation gates part of the user's apply flow. The launcher builds only when its companion executable is missing or older than the bundled source.
- To compile or package the companion, run `scripts/build-companion.ps1`. Use `-Publish` only when a portable release is requested.
- To produce the standard current-user Windows installer, run `scripts/build-installer.ps1` after installing Inno Setup 7. It emits checksums and a release manifest and can optionally use a current-user signing certificate. Keep generated artifacts in ignored `dist/`; upgrades preserve user state and uninstall must never terminate Codex.
- To estimate or measure impact, read [references/performance.md](references/performance.md) and compare stock/background runs with the documented method.
- To remove the live layer, use the companion's **Restore Codex background** action or `scripts/reset.ps1`. Read [references/security-rollback.md](references/security-rollback.md) for repair or cleanup.
- For implementation work or a Codex update regression, read [references/cdp-runtime.md](references/cdp-runtime.md).

## Operating rules

1. Diagnose before applying. This release requires Windows 11 x64 and the official `OpenAI.Codex` Store/MSIX desktop package. Package enumeration may be restricted, but a listener is accepted only when Windows reports the exact official package identity and its executable has the same file identity as `app\ChatGPT.exe` or `app\Codex.exe` in that package's staged location. The package may be installed on any local volume; unpackaged, renamed, ARM64, and x86 builds are unsupported.
2. Never close or restart Codex automatically. If Codex is already open without the startup-only wallpaper channel, preserve the selected wallpaper and ask the user to close Codex manually, then use **Start / reconnect Codex**.
3. Bind CDP to `127.0.0.1`, choose an unused high port, verify the browser identity and an `app://` Codex page, and reject any non-loopback debugger URL.
4. Never modify `WindowsApps`, `app.asar`, packaged resources, signatures, authentication state, conversations, API keys, or model settings.
5. Inject only the bundled background runtime. Keep its layer `pointer-events: none`, leave editor, terminal, syntax, warning, success, and destructive colors untouched, and make cleanup idempotent.
6. A fresh state defaults to **automatic color coordination**, zero blur, muted video, and pause-when-hidden. Saved settings are loaded on later runs and override those fresh-state defaults, so before **Apply** verify the currently visible controls rather than assuming the defaults. Palette sampling uses the already-loaded image or first decodable video frame once at 32×32 and never runs continuously.
7. Prefer a static image for the lowest load. Muted MP4/WebM at 1080p and 24/30 FPS, zero blur, and pause-when-hidden are recommendations, not enforced media properties. The companion validates static-image dimensions but does not inspect video resolution/FPS, transcode media, monitor load or battery, or automatically downgrade a direct video to a lower-cost source.
8. Wallpaper Engine Image and bounded Video projects load their validated local media directly into Codex. Large Video and Scene projects prefer Wallpaper Engine's private off-screen renderer with WGC and hardware H.264; clearly report bounded renderer or preview fallback when native rendering is unavailable. Web projects use safe previews, and Application wallpapers are never executed.
9. Never send media off-device, publish it, bundle it, or expose it through a LAN-addressable server. The companion's bounded, in-memory media transfer over loopback CDP into a page-owned Blob URL is allowed; UI/code may call that local step an upload, but it is not an Internet or off-device upload. Persist only local references and user-owned settings.
10. If validation fails after a Codex update, fail closed and restore the stock view. Do not broaden selectors or bypass origin, process, or package checks merely to make injection succeed.
11. Restore removes only this project's live visual/runtime layer and Blob URLs. It preserves presets and saved settings, does not disable CDP or close its listener/port, and does not terminate Codex. Fully exit the CDP-enabled Codex process when the user wants the debug port closed; never use `-PurgeLocalState` as part of a normal restore.

## Completion checks

- For ordinary apply/restore requests, report the diagnostic or visible companion result. Do not run development/release acceptance merely because a background was selected, tuned, or restored.
- For source changes, run `scripts/build-companion.ps1`, execute the built `CodexWallpaperSkin.dll --self-test` through `dotnet`, run `node scripts/runtime-smoke-test.mjs`, and run the `skill-creator` validator against this folder.
- For release work, also verify **Restore Codex background**, hidden-window video pause, missing-media handling, and a Codex update mismatch before publishing.
