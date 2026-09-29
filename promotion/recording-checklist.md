# Recording checklist

Record at the native display resolution and 60 FPS when stable. Keep the Codex window at the same size and position for every matched shot. Capture at least two seconds of clean handles before and after each action.

## Privacy preparation

- Create a demo project named `CWS Demo` with no personal files or history.
- Use a demo conversation containing only neutral text such as “Explain this sample function.”
- Disable notifications and close mail, chat, password-manager, and personal browser tabs.
- Hide the account menu, recent-project list, local paths, terminal history, and desktop icons containing names.
- Use Doctor's shareable/redacted view only.
- Review the final export frame by frame before publication.

## Required recordings

| Filename | Duration | Required content | Acceptance check |
|---|---:|---|---|
| `01-stock-codex.mp4` | 8 s | Stock Codex workspace, no pointer movement | Same window geometry as clip 02 |
| `02-skinned-match.mp4` | 12 s | Same workspace and camera with the strongest licensed live wallpaper | First frame aligns with clip 01 |
| `03-independent-wallpapers.mp4` | 16 s | Desktop and Codex visible with clearly different wallpapers | Independence is visually undeniable |
| `04-connect.mp4` | 10 s | Click Start / reconnect Codex and reach connected state | No endpoint or AUMID exposed |
| `05-scan.mp4` | 10 s | Scan Wallpaper Engine or Add local; results appear | No local folder path exposed |
| `06-apply.mp4` | 12 s | Select media, click Apply selected, wait for completed switch | No failure/retry hidden by a cut |
| `07-image.mp4` | 8 s | Static image with readable native interface | Label visible or added in edit |
| `08-video.mp4` | 10 s | Smooth local MP4/WebM result | Visible motion for at least 6 s |
| `09-live-scene.mp4` | 12 s | WE LIVE SCENE result | Scene animation is clearly visible |
| `10-adjustments.mp4` | 25 s | Opacity, focus, panel opacity, automatic palette, preset | One control at a time; slow pointer |
| `11-hidden-pause.mp4` | 8 s | Hide/show behavior or corresponding controller state | Do not claim zero resource usage |
| `12-doctor.mp4` | 8 s | Shareable Doctor health summary | No raw target IDs or local paths |
| `13-restore.mp4` | 10 s | Click Restore and return to stock Codex | Continuous take through completion |
| `14-github.mp4` | 12 s | Repository landing page, v0.8.1 Release, Star button | No unrelated browser history/tabs |

## Wallpaper and music clearance

For every visible background and audio track, add a row to `credits-template.md`. Do not record a third-party work merely because it is installed locally; confirm that public demonstration is allowed or obtain permission.
