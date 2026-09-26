# Compatibility

Read this file when diagnosing a machine or explaining why a source is using a fallback.

## Target

- Windows 11 x64 is the supported release target.
- The official `OpenAI.Codex` Store/MSIX desktop package is required, not merely preferred. It may be installed on the system drive or another local volume. The companion accepts a listener only after Windows reports the exact official package identity and the listener executable has the same file identity as that package's staged `app\ChatGPT.exe` or `Codex.exe`.
- Unpackaged/renamed copies and ARM64/x86 releases are unsupported. The companion and published artifact target `win-x64`.
- .NET 8 is needed only to build from source. A self-contained published build includes its runtime.
- Wallpaper Engine is optional for local images and videos.

## Media matrix

| Source | Status | Runtime path |
| --- | --- | --- |
| Local PNG/JPEG/WebP | Supported | Decode once and inject as a renderer Blob URL. |
| Local MP4/WebM | Supported | Transfer once, create a looping `<video>`, and honor the current mute/pause settings. Resolution and FPS recommendations are not enforced. |
| Animated GIF | Supported within the media safety limits | Decode and animate in Codex; MP4/WebM remains preferable for large or long animations. |
| Wallpaper Engine Image | Supported when its referenced image is readable | Codex loads the installed local file independently of desktop playback. |
| Wallpaper Engine Video | Supported for MP4/WebM | Files up to 256 MiB load directly; larger projects use Wallpaper Engine `playInWindow` plus the native capture path. |
| Wallpaper Engine Scene | Supported with Wallpaper Engine | Render in a bounded private `playInWindow` surface; prefer Windows Graphics Capture/D3D11 and fall back to the guarded compatibility capture path. The built-in Scene renderer or preview remains an explicitly labeled final fallback. |
| Wallpaper Engine Web | Static fallback | Use the installed preview image. Never execute third-party wallpaper JavaScript in the Codex renderer. |
| Wallpaper Engine Application | Rejected | Never launch an arbitrary wallpaper executable. |

## Expected fallbacks

- A missing source file keeps the current background unchanged and marks the preset unavailable.
- A signature mismatch, image over 32 MiB, directly uploaded video over 256 MiB, image side over 8192 pixels, or image over 33,554,432 total pixels is rejected before local CDP transfer. Large Wallpaper Engine MP4/WebM projects may instead use the bounded native-capture path. Valid media within the applicable limits is not automatically transcoded.
- Scene/Web without a valid preview remains selectable only for inspection; it cannot be applied.
- An unsupported Codex page structure fails closed instead of applying broad transparency selectors.
- A running Codex without verified loopback CDP flags must be fully exited by the user before **Start / reconnect Codex** can start an enhanced instance; the companion does not terminate it.
- Saved settings are restored on startup and override fresh-state defaults. Verify the visible automatic-palette, blur, mute, and pause-when-hidden controls before **Apply selected**.

## Discovery paths

Wallpaper Engine projects normally live below Steam library roots at:

```text
steamapps/workshop/content/431960/<workshop-id>/project.json
steamapps/common/wallpaper_engine/projects/myprojects/<project>/project.json
```

Read `project.json.type` and `project.json.file`; do not infer a project type from filenames. Resolve real paths and reject links or junctions that escape the recognized project directory.

Official Wallpaper Engine references:

- [Command-line control](https://help.wallpaperengine.io/en/functionality/cli.html)
- [Editing and packaged-scene limits](https://help.wallpaperengine.io/en/functionality/editingwallpapers.html)
