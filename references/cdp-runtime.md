# CDP runtime

Read this file only when maintaining the injection engine or adapting to a Codex update.

## Session shape

1. Require Windows 11 x64. Discover the official `OpenAI.Codex` Store/MSIX package or an already running process whose Windows package identity verifies as that package; do not accept unpackaged or renamed copies. Do not assume that the package is installed on the system drive.
2. If needed and approved, activate its AUMID with:

```text
--remote-debugging-address=127.0.0.1 --remote-debugging-port=<unused-high-port>
```

3. Resolve the listening PID from the Windows TCP table. Query that process handle for its package family and full name, resolve the staged package location, and require the listener image to be the same file as the package's exact `app\ChatGPT.exe` or `app\Codex.exe`. Compare Windows file identity rather than path strings because Windows may expose a system-drive alias for a package physically stored on another volume.
4. Read `/json/version` and `/json/list` from the loopback endpoint.
5. Require a same-port page WebSocket URL whose path matches `/devtools/page/<id>`.
6. Select an `app://` page only after the expected Codex surface-theme marker exists.
7. Use `Runtime.evaluate` only for the bundled idempotent installer. Treat navigation or reload as a disconnected skin that must be explicitly re-applied after identity checks.

## Background layer contract

- One fixed, full-window root at the back of the renderer.
- One `<img>` or looping `<video>` with `object-fit`, focal-point, and current mute controls.
- One optional black veil plus palette-tinted translucent surfaces for readability.
- `pointer-events: none`, `aria-hidden="true"`, and no focusable descendants.
- Native Codex content remains above the background. Automatic palette mode changes semantic interface variables only; do not override terminal, editor, syntax, warning, success, or destructive colors.
- Media becomes a page-owned Blob URL transferred in bounded in-memory chunks over loopback CDP. This local transfer is permitted even if UI/code calls it an upload; never send the bytes off-device. Revoke the previous URL on switch and restore.
- Pause video on `visibilitychange` when the setting is enabled.

The palette extractor draws the already decoded media to a 32×32 canvas once. It does not inspect conversations, DOM text, cookies, storage, or network traffic and does not run on every video frame.

The runtime must expose a versioned marker and an idempotent cleanup function. Installing twice updates the existing layer instead of stacking nodes, observers, or video decoders.

Cleanup owns only the injected runtime: visual nodes, styles, observers/listeners, queued work, Blob URLs, markers, and `--cws-*` state. It must not delete presets or saved settings, disable CDP, close the listener, or terminate Codex. The loopback debug port remains open until the CDP-enabled Codex process fully exits.

## Selector policy

Limit surface markers to direct body children and semantic `main`, `aside`, `nav`, dialog, or region containers that occupy a meaningful part of the viewport. Exclude editors, code, inputs, and content-editable elements. An unrecognized shell is a compatibility failure, not permission to make every page element transparent.

## Development acceptance

For injection-engine or Codex-compatibility changes, run all of the following; they are development gates, not steps in an ordinary apply/restore flow:

```powershell
pwsh -NoProfile -File .\scripts\build-companion.ps1
dotnet .\companion\bin\Debug\net8.0-windows\win-x64\CodexWallpaperSkin.dll --self-test
node .\scripts\runtime-smoke-test.mjs
```

Also run the `skill-creator` validator when the skill documentation changes. Release qualification additionally covers restore, hidden-window pause, missing media, and a simulated Codex surface mismatch.

## Community evidence

The architecture is independently demonstrated by open-source community implementations:

- [Backdrop for Codex](https://github.com/TogawaSakiko-desuwa/backdrop-for-codex) (Apache-2.0)
- [Codex Dynamic Skin](https://github.com/CCDawn/Codex-Dynamic-Skin) (MIT)
- [Codex Skins discussion in openai/codex](https://github.com/openai/codex/discussions/34350)

These projects are research references, not bundled runtime dependencies. Do not copy code without preserving its license and attribution.
