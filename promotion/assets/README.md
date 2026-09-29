# Generated promotion artwork

These files are generated from the checked-in project logo plus programmatic geometry and gradients. They intentionally contain no third-party wallpaper and no simulated product interface:

- `social-preview.jpg` — 1280×640, suitable for GitHub Settings → General → Social preview.
- `bilibili-cover.jpg` — 1920×1080 horizontal cover source.
- `vertical-cover.jpg` — 1080×1920 short-video cover source.
- `compatibility-card.jpg` — 1920×1080 platform/known-boundary card for 02:08–02:22.
- `end-card.jpg` — 1920×1080 GitHub call-to-action card for the final five seconds.

Regenerate all five from the repository root with:

```powershell
pwsh -NoProfile -File .\scripts\build-promotion-assets.ps1
```

Use these files as rights-safe brand cards. The trailer's product-effect shots must still use real Codex recordings whose wallpaper rights are documented in `promotion/credits-template.md`.
