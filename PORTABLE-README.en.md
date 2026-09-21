# Codex Wallpaper Skin portable build

This is a self-contained Windows 11 x64 companion. It needs no Python, PyYAML, or .NET installation. This release requires the official x64 `OpenAI.Codex` Store/MSIX desktop package; unpackaged or renamed copies and ARM64/x86 builds are unsupported. Download it only from this project's trusted release page, extract the complete ZIP, and then run `CodexWallpaperSkin.exe`.

## Use a Wallpaper Engine wallpaper

1. Subscribe to or select a wallpaper in Wallpaper Engine and let its local files finish downloading.
2. Run `CodexWallpaperSkin.exe`; choose **Detect app**, then **Activate with CDP**, wait for Codex to open, and choose **Connect**.
3. If Codex is already running normally, select a wallpaper and choose **Apply selected**. The app queues it without terminating the active task. For immediate use, save or pause active work and choose **Restart Codex normally and apply now**; the app requests a normal shutdown and never force-terminates Codex.
4. Choose **Scan Wallpaper Engine**, select the matching project, review automatic palette, blur, mute, and other controls, then choose **Apply selected**.
5. Use **Restore Codex background** to remove this project's page layer. Restore does not close the CDP port; fully exit that Codex process to close it.

The last successful Apply is remembered. Enable **Install ‘Codex with remembered wallpaper’ on the Desktop** to create a separate desktop entry. Opening Codex from that entry prepares the startup-only wallpaper channel, restores the last wallpaper, and keeps live playback running in the background without first showing the adjustment window. The official Codex shortcut is never replaced because Chromium cannot add this channel after the process has started. If Codex was opened from the official entry first, the new launcher offers a confirmed normal restart and never force-terminates it. Enabling Windows sign-in restore as well is recommended. Recreate the shortcut after moving the portable folder. Click any displayed setting value for exact numeric entry. **Restore Codex background** clears both remembered and queued state.

Use **Search**, the type filter, and the collection filter above the list to narrow a large library. After selecting a wallpaper, **Rename** changes only its display name in this app and **Set collection** creates personal groups such as Relaxing, Anime, Landscape, or Work. Rescanning preserves this organization and never renames Steam Workshop projects or local files.

Image/Video projects use their original installed media (up to the 256 MiB video safety limit). Scene projects prefer Wallpaper Engine's own private off-screen renderer. That surface remains capturable without appearing in the taskbar or Alt+Tab and without taking focus. Wallpaper Engine must remain available in the background. For stability, the current H.264 path does not reproduce pointer interaction. If native rendering is unavailable, the app explicitly falls back to the bounded renderer or a safe preview. Web projects use safe previews only, and Application projects are never executed.

## Command line and verification

Open PowerShell in this directory:

```powershell
.\CodexWallpaperSkin.exe --doctor --json
.\CodexWallpaperSkin.exe --self-test
.\CodexWallpaperSkin.exe --restore
Get-FileHash ..\CodexWallpaperSkin-win-x64.zip -Algorithm SHA256
```

Compare the last result with the `.sha256` file beside the release archive. Do not disable PowerShell execution policy or Windows security controls system-wide.

Video and Scene backgrounds add decode, Windows Graphics Capture/D3D11, GPU/CPU, and battery cost. Scene playback targets 60 FPS and falls back to 30 FPS or the lower Wallpaper Engine global limit when necessary; unavailable WGC/hardware H.264 is explicitly reported before compatibility capture. **Brighter high-clarity preset** selects full capture scale, removes the veil and blur, reduces panel dimming, and applies mild display compensation. It cannot eliminate every difference between H.264 4:2:0 and Wallpaper Engine's direct desktop composition. A static image, zero blur, lower Scene scale, and pause-when-hidden are the lightest choices. Frames remain in the local controlled path and are not uploaded by this app. CDP is unauthenticated to other processes running as the same Windows user, so enable it only in a trusted session.

No Wallpaper Engine media is bundled. You must own Wallpaper Engine and follow each wallpaper author's license. Report security issues privately using the bundled `SECURITY.md` process.
