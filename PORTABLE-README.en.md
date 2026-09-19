# Codex Wallpaper Skin portable build

This is a self-contained Windows 11 x64 companion. It needs no Python, PyYAML, or .NET installation. This release requires the official x64 `OpenAI.Codex` Store/MSIX desktop package; unpackaged or renamed copies and ARM64/x86 builds are unsupported. Download it only from this project's trusted release page, extract the complete ZIP, and then run `CodexWallpaperSkin.exe`.

## Use a Wallpaper Engine wallpaper

1. Subscribe to or select a wallpaper in Wallpaper Engine and let its local files finish downloading.
2. Run `CodexWallpaperSkin.exe`; choose **Detect app**, then **Activate with CDP**, wait for Codex to open, and choose **Connect**.
3. If Codex is already running normally, select a wallpaper and choose **Apply selected**. The app queues it without terminating the active task and restores it after your next natural Codex exit. Exit Codex yourself first only when you need immediate application.
4. Choose **Scan Wallpaper Engine**, select the matching project, review automatic palette, blur, mute, and other controls, then choose **Apply selected**.
5. Use **Restore Codex background** to remove this project's page layer. Restore does not close the CDP port; fully exit that Codex process to close it.

The last successful Apply is remembered and restored the next time the controller opens or reconnects. Background settings also offers an opt-in Windows sign-in restore. Click any displayed setting value for exact numeric entry. **Restore Codex background** clears both remembered and queued state. A normally started Codex instance is detected immediately and deferred safely instead of failing after 30 seconds.

Image/Video projects use their original installed media (up to the 256 MiB video safety limit). Scene projects prefer Wallpaper Engine's own private off-screen renderer; Codex pointer coordinates are forwarded to it, preserving native water feedback, Puppet Warp, particles, scripts, and audio response. Wallpaper Engine must remain available in the background. If native rendering is unavailable, the app explicitly falls back to the bounded renderer or a safe preview. Web projects use safe previews only, and Application projects are never executed.

## Command line and verification

Open PowerShell in this directory:

```powershell
.\CodexWallpaperSkin.exe --doctor --json
.\CodexWallpaperSkin.exe --self-test
.\CodexWallpaperSkin.exe --restore
Get-FileHash ..\CodexWallpaperSkin-win-x64.zip -Algorithm SHA256
```

Compare the last result with the `.sha256` file beside the release archive. Do not disable PowerShell execution policy or Windows security controls system-wide.

Video and Scene backgrounds add decode, capture, GPU/CPU, and battery cost; high-fidelity Scene transfer is capped at 15 FPS. A static image, zero blur, lower Scene scale, and pause-when-hidden are the lightest choices. Frames travel only over `127.0.0.1` into Codex renderer memory and are not uploaded by this app. CDP is unauthenticated to other processes running as the same Windows user, so enable it only in a trusted session.

No Wallpaper Engine media is bundled. You must own Wallpaper Engine and follow each wallpaper author's license. Report security issues privately using the bundled `SECURITY.md` process.
