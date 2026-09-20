# Performance

Read this file when choosing static versus dynamic media, investigating lag, or preparing a release benchmark.

## Practical impact

A skin cannot slow the remote model's inference. It can consume local CPU, GPU, VRAM, memory, and battery, which may make scrolling, typing, resizing, or other GPU applications less responsive.

From lowest to highest typical cost:

1. Static PNG/JPEG/WebP: one decode plus one composited texture. A decoded RGBA surface is approximately 7.9 MiB at 1080p, 14.1 MiB at 1440p, and 31.6 MiB at 4K before compositor overhead.
2. MP4/WebM: continuous decode and composition. Hardware decode depends on the codec, driver, and machine; muted 1080p at 24/30 FPS is a recommendation.
3. Wallpaper Engine Scene capture: Wallpaper Engine renders, Windows Graphics Capture/D3D11 reads the private window (or the guarded compatibility path is used), the bridge encodes, and Chromium decodes again. Cost varies by wallpaper and capture scale.
4. Large real-time `backdrop-filter: blur(...)`: often more expensive than a neutral alpha veil. Zero blur is recommended.

Do not advertise zero overhead. Static media should normally be close to unnoticeable, while dynamic results remain machine- and wallpaper-dependent.

## Low-load recommendations

- Choose static media when the lowest load matters.
- MP4/WebM only; do not animate GIF.
- 1920×1080 at 24/30 FPS on battery or an integrated GPU.
- Prefer no more than 2560×1440 at 30 FPS.
- Muted, non-interactive, `pointer-events: none`.
- Pause video on `document.hidden`; release it when restoring the stock view.
- Sample the palette once at 32×32; never analyze every video frame.
- Use translucent palette surfaces plus an optional black alpha veil for readability. Blur remains `0` unless requested.
- Do not play the same Wallpaper Engine video both on the desktop and again inside Codex unless the user accepts the duplicate decode.

These are operator recommendations, not automatic runtime guarantees. The companion validates format, signature, file-size limits, and static-image dimensions, but does not inspect video resolution/FPS, transcode, watch CPU/GPU/battery, or automatically downgrade a direct video. Fresh-state defaults do not overwrite saved settings, so check the visible controls before **Apply**.

## A/B measurement

Measure relative to a stock Codex baseline on the same power plan, monitor layout, GPU, resolution, and brightness.

1. Warm up each mode for two minutes.
2. Sample five minutes idle and five minutes of the same typing, scrolling, and resize workload.
3. Test stock, static, video, and any experimental mode in randomized order; repeat each three times.
4. Record the Codex process tree plus the companion and Wallpaper Engine: CPU, private working set, GPU 3D, video decode, dedicated/shared VRAM, and battery discharge where available.
5. For release qualification, use PresentMon or WPR/WPA rather than a single Task Manager screenshot.

Suggested downgrade gates are release-measurement criteria, not universal standards and not behavior implemented by the companion:

- Video dropped-frame ratio above 1%.
- Sustained system CPU increase above 3 percentage points or companion/background process p95 above 5%.
- DXGI memory usage above 80% of budget warns; above 85% downgrades.
- More than 100 MiB additional private working set for static or 250 MiB for dynamic.
- On Battery Saver, manually choose a static image or an available static preview before applying a dynamic source.

When troubleshooting manually, reduce cost in this order:

```text
current video -> manually choose a lower-resolution/lower-frame-rate video -> static image/available preview -> stock background
```

There is no automatic “dynamic low” mode, transcoder, or adaptive fallback in this release.

Primary references:

- [Wallpaper Engine video performance](https://help.wallpaperengine.io/en/videos/performance.html)
- [Wallpaper Engine GPU interpretation](https://help.wallpaperengine.io/en/performance/gpu.html)
- [Wallpaper Engine pause/stop behavior](https://help.wallpaperengine.io/en/performance/game.html)
- [Microsoft DWM performance guidance](https://learn.microsoft.com/en-us/windows/win32/dwm/bestpractices-ovw)
- [PresentMon](https://github.com/GameTechDev/PresentMon)
- [DXGI video-memory budgets](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/ns-dxgi1_4-dxgi_query_video_memory_info)
