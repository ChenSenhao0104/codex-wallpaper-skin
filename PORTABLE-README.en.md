# Codex Wallpaper Skin portable build

This is a self-contained Windows 11 x64 companion. It needs no Python, PyYAML, or .NET installation. This release requires the official x64 `OpenAI.Codex` Store/MSIX desktop package; unpackaged or renamed copies and ARM64/x86 builds are unsupported. Download it only from this project's trusted release page, extract the complete ZIP, and then run `CodexWallpaperSkin.exe`.

This directory is the portable distribution and does not register an uninstaller. Use the matching `CodexWallpaperSkin-Setup-vX.Y.Z-win-x64.exe` instead when Start-menu integration, in-place upgrades, and standard Windows uninstall are preferred.

## Use a Wallpaper Engine wallpaper

1. Subscribe to or select a wallpaper in Wallpaper Engine and let its local files finish downloading.
2. Run `CodexWallpaperSkin.exe` and choose **Start / reconnect Codex**. Detection, controlled startup, and reconnection are automatic.
3. If Codex is already running normally, close Codex manually and then choose **Start / reconnect Codex**. The controller never closes or restarts Codex automatically.
4. Choose **Scan Wallpaper Engine**, select the matching project, review automatic palette, blur, mute, and other controls, then choose **Apply selected**.
5. Use **Restore Codex background** to remove this project's page layer. Restore does not close the CDP port; fully exit that Codex process to close it.

The last successful Apply is remembered. Start or connect Codex through this controller whenever you want the wallpaper channel. If Codex is already running normally, close it manually and choose **Start / reconnect Codex**. This release safely removes the legacy `Codex with remembered wallpaper` desktop shortcut created by older builds and never changes the official Codex shortcut. The top language button switches between English and Chinese and remembers the choice. Click any displayed setting value for exact numeric entry. **Restore Codex background** clears both remembered and pending state.

Use **Search**, the type filter, and the collection filter above the list to narrow a large library. After selecting a wallpaper, **Rename** changes only its display name in this app and **Set collection** creates personal groups such as Relaxing, Anime, Landscape, or Work. Rescanning preserves this organization and never renames Steam Workshop projects or local files.

The right-side **Performance profile** offers Power saver, Balanced, and High quality plans; changing an individual quality control switches the label to Custom. **Assign preset to wallpaper** binds the selected visual preset to one wallpaper. **Doctor** can export a diagnostic ZIP that excludes wallpaper media, the complete state file, and Codex task titles.

Image and Video projects up to 256 MiB use their original installed media directly. Larger Wallpaper Engine videos and Scene projects use Wallpaper Engine's private off-screen renderer and the native capture path. That surface remains capturable without appearing in the taskbar or Alt+Tab and without taking focus. Wallpaper Engine must remain available in the background. For stability, the current H.264 path does not reproduce pointer interaction. If native rendering is unavailable, the app explicitly falls back to the bounded renderer or a safe preview. Web projects use safe previews only, and Application projects are never executed.

## Command line and verification

Open PowerShell in this directory:

```powershell
.\CodexWallpaperSkin.exe --doctor --json
.\CodexWallpaperSkin.exe --self-test
.\CodexWallpaperSkin.exe --restore
Get-FileHash ..\CodexWallpaperSkin-win-x64.zip -Algorithm SHA256
```

Compare the last result with the `.sha256` file beside the release archive. Do not disable PowerShell execution policy or Windows security controls system-wide.

Video and Scene backgrounds add decode, Windows Graphics Capture/D3D11, GPU/CPU, and battery cost. Scene playback targets 60 FPS and falls back to 30 FPS or the lower Wallpaper Engine global limit when necessary; unavailable WGC/hardware H.264 is explicitly reported before compatibility capture. Choose among named plans in **Visual preset**, create or edit them through **Manage presets…**, and use **Apply preset**; every right-side control remains editable afterward. Presets cannot eliminate every difference between H.264 4:2:0 and Wallpaper Engine's direct desktop composition. A static image, zero blur, lower Scene scale, and pause-when-hidden are the lightest choices. Frames remain in the local controlled path and are not uploaded by this app. CDP is unauthenticated to other processes running as the same Windows user, so enable it only in a trusted session.

No Wallpaper Engine media is bundled. You must own Wallpaper Engine and follow each wallpaper author's license. Report security issues privately using the bundled `SECURITY.md` process.
