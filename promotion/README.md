# Codex Wallpaper Skin promotion kit

This directory contains the production-ready plan, narration, subtitles, publishing copy, and measurement checklist for the first public promotional campaign.

## Deliverables

| Deliverable | Source | Status |
|---|---|---|
| 2:35 Chinese 16:9 main film | `storyboard.md`, `voiceover.zh-CN.md`, `subtitles.zh-CN.srt` | Script complete; real UI shots still need recording |
| English-subtitled main film | `subtitles.en.srt` | Subtitle draft complete |
| 45-second vertical cut | `vertical-45s.md`, `vertical-45s.zh-CN.srt`, `vertical-45s.en.srt` | Edit spec complete |
| 15-second visual loop | `storyboard.md` section “15-second loop” | Edit spec complete |
| Bilibili, Reddit, GitHub copy | `publishing-copy.md` | Ready to publish after links and credits are verified |
| GitHub Social Preview and Bilibili cover | `assets/` | Generated from real project screenshots |
| Campaign measurement | `metrics-template.md` | Ready for day 0/3/7/14 entries |

## Production folders

Create recordings under `promotion/raw/` and exports under `promotion/renders/`. Both directories are intentionally ignored by Git because they may contain large files, local paths, account details, or third-party artwork. Do not commit source recordings or final videos without a separate redistribution review.

Use the exact filenames in `recording-checklist.md`. This keeps the storyboard decision-complete and makes it possible for any editor to assemble the film without guessing which shot is which.

## Non-negotiable rules

- Real Codex UI must be shown. Do not replace missing product footage with generated or mocked interfaces.
- Use a clean demo account/project and review every frame for private chats, filenames, notifications, email addresses, browser history, tokens, and local paths.
- Only use original, licensed, or expressly permitted wallpaper and music. Record the creator, source URL, license, and permission in `credits-template.md`.
- State the current product boundary: Windows 11 x64, official x64 OpenAI.Codex Store/MSIX package, public Beta, unsigned installer.
- Do not claim guaranteed 60 FPS, pointer interaction, macOS/Linux support, or official affiliation.

## Build the static campaign artwork

Run from the repository root:

```powershell
pwsh -NoProfile -File .\scripts\build-promotion-assets.ps1
```

The script reads the checked-in project logo and generates the remaining geometry and gradients itself. It regenerates the rights-safe JPEG files in `promotion/assets/` without embedding a third-party wallpaper or product-interface screenshot.
