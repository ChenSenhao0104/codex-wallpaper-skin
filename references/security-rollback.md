# Security and rollback

Read this file before changing runtime security checks, repairing a failed launch, or deleting local state.

## Trust boundary

- The official Codex package is not modified.
- The companion connects only to the canonical `http://127.0.0.1:<port>` host and same-port `ws://127.0.0.1:<port>/devtools/page/...` endpoints. Hostnames such as `localhost` and IPv6 loopback forms are deliberately rejected so HTTP, WebSocket, and Windows listener-PID checks address the same socket.
- A loopback CDP port is not authenticated against other processes running as the same Windows user. Keep the enhanced session short and do not run untrusted local programs at the same time.
- Resolve the listener PID, require Windows to report the exact official x64 MSIX package identity, and compare the listener executable's Windows file identity with `app\ChatGPT.exe` or `app\Codex.exe` in that package's staged location. This avoids trusting a look-alike path and also handles system-drive aliases for packages installed on another volume. Then accept only same-port `/devtools/page/...` WebSockets for an `app://` page after checking `/json/version`; re-check the listener owner after the WebSocket is established and verify Codex surface markers again before mutation.
- Do not read cookies, local storage, IndexedDB, request bodies, conversations, authentication state, or model configuration.
- Inject only bundled constant JavaScript plus validated scalar settings and transferred local media bytes. Never evaluate wallpaper-supplied JavaScript.

## Media validation

- Resolve the selected file to a full path and require a normal local file.
- For Wallpaper Engine, ensure the path remains below the recognized project directory, reject reparse-point escapes, and repeat that containment check immediately before opening one validated read handle for transfer.
- Allow only PNG/JPEG/WebP/MP4/WebM with matching signatures and bounded file sizes.
- Reject Application wallpapers and unknown project types.
- Never send media off-device or expose it from a LAN-addressable server. The bounded, in-memory transfer over `127.0.0.1` CDP into a renderer-owned Blob URL is permitted; names such as `Upload` in the UI/code refer only to this local transfer.

## Restore sequence

1. Connect to the recorded and re-verified CDP browser/page.
2. Invoke the bundled idempotent cleanup function. It removes only this project's nodes, style, observer, queued animation frame, event listener, Blob URLs, surface markers, and root variables/classes.
3. Confirm the marker is absent and the expected native Codex shell remains.
4. Preserve presets, saved settings, and source media. Restore does not disable CDP, close its listener, or terminate Codex; fully exit the CDP-enabled Codex process to close the debug port.
5. Never terminate unrelated processes or a listener whose process/browser identity is not the recorded one.

`scripts/reset.ps1` performs live cleanup through the companion and preserves local state. `-PurgeLocalState` is a separate destructive option, prompts by default, validates that the target is exactly `%LOCALAPPDATA%\CodexWallpaperSkin`, and never touches Codex data. Never use it for a normal restore.

## Update failure

When a Codex update changes the DOM:

- Stop applying the skin.
- Keep existing presets and media references.
- Restore the stock view through stable IDs and the runtime cleanup function.
- Update selectors only after inspecting the new official renderer and adding a regression fixture.
- Never solve the mismatch with broad `div`, wildcard, or text-content selectors.
