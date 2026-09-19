# Codex Wallpaper Skin

English | [简体中文](README.md)

An independent Windows 11 x64 desktop application that adds local images, videos, and installed Wallpaper Engine projects as Codex Desktop backgrounds. The current GUI release is `0.3.0` and requires the official x64 `OpenAI.Codex` Store/MSIX package.

The application uses a loopback-only Chrome DevTools Protocol (CDP) session to add a reversible background layer to the real Codex page. Codex loads Image/Video media independently; complex Scene projects are rendered natively by a private local Wallpaper Engine window and transferred to Codex. The application does not patch `WindowsApps`, `app.asar`, the official signature, chats, or authentication data.

> This is an unofficial project and is not affiliated with or endorsed by OpenAI, Valve, or Wallpaper Engine.

## Current GUI capabilities

- PNG, JPEG, WebP, GIF, MP4, and WebM media.
- Discovery of Wallpaper Engine Image, Video, Scene, and Web projects across local Steam libraries.
- Original local media for Image and Video projects. The direct-video safety limit is 256 MiB so common 2K/4K videos are not incorrectly replaced with square thumbnails or low-resolution GIFs.
- Scene projects prefer Wallpaper Engine's official `playInWindow` renderer and bridge its off-screen output plus Codex pointer coordinates. Puppet Warp, particles, author scripts, feedback effects, and audio response therefore keep their native semantics.
- If the native renderer is unavailable, the bounded built-in 2D renderer is used with an explicit compatibility warning, followed by an original package texture or validated Workshop preview when necessary.
- Web code is never executed; only validated animated/static previews are allowed. Application projects are always rejected.
- One-time 32×32 palette sampling for translucent surfaces, accents, and inherited interface text while code, terminal, warning, and status colors remain intact.
- Fit, focal point, opacity, readability veil, brightness, contrast, saturation, palette strength, panel opacity, inherited-text coordination, blur, animation speed, Scene FPS, and Scene render-scale controls. Click a displayed numeric value for exact entry.
- Remembers the last successfully applied wallpaper and restores it on reconnect or Windows sign-in. If Codex is already running normally, restoration is queued until the user closes it naturally, without interrupting the active task.
- Decode-before-swap, preservation of the old background on failure, pause when hidden, bounded cleanup, and **Restore Codex background**.

Complex and interactive Scenes are rendered by Wallpaper Engine itself. Codex pointer movement and button state are mapped to the private render window, so water feedback, parallax, and similar interactions remain the wallpaper's own implementation. Wallpaper Engine must be installed and remain available in the background; closing the visible controller hands playback to a hidden restore worker. The built-in renderer remains only as an explicitly labeled fallback.

## Quick start

Download `CodexWallpaperSkin-win-x64.zip` and its `.sha256` file from Releases, verify the hash, extract the complete archive, and run `CodexWallpaperSkin.exe`. The portable application is self-contained and needs no Python, PyYAML, Node.js, or .NET installation.

1. Run the application and choose **Detect app**, then **Activate with CDP**.
2. If Codex is already running normally, select a wallpaper and choose **Apply selected**. It is queued without closing Codex and restores after the next natural exit. To apply immediately, save your work and exit Codex yourself first.
3. After Codex reopens, choose **Connect**.
4. Choose **Scan Wallpaper Engine**, select a wallpaper, and review the controls.
5. Choose **Apply selected**. Use **Restore Codex background** when finished.

A fresh state enables palette coordination, inherited-text coordination, mute, and pause-when-hidden. Opacity, brightness, contrast, and saturation start at their original values; the black veil and blur start at `0`. **Original color / clarity** restores all image-affecting controls to neutral values.

After the first successful Apply, the controller remembers that wallpaper. Windows cannot dynamically add Chromium debugging flags to an existing Codex process, so safe in-place injection is impossible in that state. The app detects it immediately, stores a pending selection, and waits for the user's natural Codex exit before the next controlled start. Windows sign-in restore is optional.

## Wallpaper Engine labels

| Label | Behavior |
|---|---|
| `[IMAGE]` | Loads the project's original image independently. |
| `[VIDEO]` | Loops the project's MP4/WebM independently. |
| `[WE LIVE SCENE]` | Uses native Wallpaper Engine rendering and bridges animation/pointer interaction, with an explicit fallback if unavailable. |
| `[ANIMATED PREVIEW]` | Uses a validated GIF preview if the package is unavailable. |
| `[STATIC FALLBACK]` | Uses a validated static preview. |
| `[REJECTED]` | The project type or media failed the safety boundary and cannot be applied. |

## Performance and security

Static images have the lowest overhead. Video decodes in Codex. High-fidelity Scenes use Wallpaper Engine rendering, Windows window capture, JPEG encoding, and loopback transfer, capped safely at 15 FPS and controlled by the 50%–100% render scale. Keep blur at `0`, choose the 15 FPS target, lower Scene scale, and enable pause-when-hidden for a lighter setup.

Frames travel only through loopback CDP into Codex renderer memory and are never uploaded by this app. High-fidelity Scenes execute inside the user's installed Wallpaper Engine and follow its security/performance settings; this app does not interpret SceneScript itself. Web wallpaper code and Application wallpapers are never run. Other processes under the same Windows user can still reach an unauthenticated CDP port, so use it only in a trusted session and fully exit the CDP-enabled Codex process to close the port.

No Wallpaper Engine media is bundled or redistributed. Users must own Wallpaper Engine and follow each wallpaper author's license. See [SECURITY.md](SECURITY.md) for private vulnerability reporting.

## Development and verification

Building requires the .NET 8 SDK; the full runtime test suite also needs Node.js. The GUI has no third-party NuGet dependency.

```powershell
pwsh -NoProfile -File .\scripts\build-companion.ps1
dotnet .\companion\bin\Debug\net8.0-windows\win-x64\CodexWallpaperSkin.dll --self-test
node .\scripts\runtime-smoke-test.mjs
```

On a development machine with Wallpaper Engine scenes installed:

```powershell
node .\scripts\scene-render-smoke-test.mjs
```

Create the self-contained executable, portable ZIP, and SHA-256 file with:

```powershell
pwsh -NoProfile -File .\scripts\build-companion.ps1 -Configuration Release -Publish
```

`SKILL.md`, `agents/`, and `references/` are retained only as possible future Codex integration entry points. They are not part of the current GUI delivery and are not used by the portable application. See the [Apache-2.0 license](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md).
