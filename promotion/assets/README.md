# Generated promotion artwork

These files are generated from the checked-in project logo and real Codex Wallpaper Skin showcase screenshot:

- `social-preview.jpg` — 1280×640, suitable for GitHub Settings → General → Social preview.
- `bilibili-cover.jpg` — 1920×1080 horizontal cover source.
- `vertical-cover.jpg` — 1080×1920 short-video cover source.
- `compatibility-card.jpg` — 1920×1080 platform/known-boundary card for 02:08–02:22.
- `end-card.jpg` — 1920×1080 GitHub call-to-action card for the final five seconds.

Regenerate all three from the repository root with:

```powershell
pwsh -NoProfile -File .\scripts\build-promotion-assets.ps1
```

The files show real project output. Any third-party artwork or application interface visible inside the screenshot retains its original rights status as described in `docs/images/showcase/README.md`.
