# Implementation Convergence Record

This record compares product behavior and implementation components. It must not rank people, models, or tools. The desired outcome is the strongest maintainable product, which may use either candidate, a deliberate hybrid, or neither.

## Task identity

- Issue:
- Milestone:
- Shared starting commit:
- Candidate pull requests:
- Evaluator and date:

## Hard product gates

Mark each gate `pass`, `fail`, or `not applicable`. Any applicable failure blocks integration.

| Gate | Candidate A | Candidate B | Converged candidate | Evidence |
| --- | --- | --- | --- | --- |
| No unintended crop, severe blur, or color distortion |  |  |  |  |
| Interactive wallpaper remains interactive where the backend promises it |  |  |  |  |
| Codex can already be running when the companion connects |  |  |  |  |
| Empty, gray, stale, and low-quality frames fail safely |  |  |  |  |
| Last known-good frame and fallback behavior are reliable |  |  |  |  |
| Restore removes the complete skin layer without modifying Codex files |  |  |  |  |
| Existing image and ordinary video paths do not regress |  |  |  |  |
| Application wallpapers remain rejected |  |  |  |  |
| No credentials, private paths, Workshop media, or personal screenshots are committed |  |  |  |  |
| Automated checks pass |  |  |  |  |

## Component decisions

For each row choose `A`, `B`, `hybrid`, or `neither`. Explain why the choice improves the product and its maintenance cost.

| Component | A summary | B summary | Decision | Product rationale |
| --- | --- | --- | --- | --- |
| Wallpaper Engine process/window lifecycle |  |  |  |  |
| Capture API and frame transport |  |  |  |  |
| Scale, crop, DPI, and coordinate mapping |  |  |  |  |
| Mouse move/down/up/wheel forwarding |  |  |  |  |
| Frame quality detection |  |  |  |  |
| Recovery and last-good-frame handling |  |  |  |  |
| Web wallpaper safety boundary |  |  |  |  |
| Backend selection and fallback |  |  |  |  |
| GUI status and diagnostics |  |  |  |  |
| Tests and maintainability |  |  |  |  |

## Measurements

Use the same machine, wallpaper, Codex window size, duration, and sampling method for every candidate.

| Measurement | Baseline | Candidate A | Candidate B | Converged candidate |
| --- | --- | --- | --- | --- |
| Capture resolution |  |  |  |  |
| Average / minimum FPS |  |  |  |  |
| Dropped or rejected frames |  |  |  |  |
| Pointer-to-visual latency |  |  |  |  |
| Companion CPU |  |  |  |  |
| Wallpaper Engine CPU/GPU |  |  |  |  |
| Memory |  |  |  |  |
| Recovery time |  |  |  |  |

## Compatibility matrix

| Case | Expected backend | A | B | Converged candidate | Notes |
| --- | --- | --- | --- | --- | --- |
| Local static image | Native image |  |  |  |  |
| Local ordinary video | Native video |  |  |  |  |
| Wallpaper Engine video | Native video or capture fallback |  |  |  |  |
| Simple Wallpaper Engine scene | Safe scene or native capture |  |  |  |  |
| Complex interactive scene | Native Wallpaper Engine capture |  |  |  |  |
| Controlled web wallpaper | Native capture with explicit safety confirmation |  |  |  |  |
| Application wallpaper | Rejected |  |  |  |  |
| Capture unavailable | Static/preview fallback with clear status |  |  |  |  |

## Final integration decision

- Selected component combination:
- Rejected approaches and reasons:
- Remaining risks:
- Required follow-up Issues:
- Full acceptance rerun result:
- Maintainer approval:
