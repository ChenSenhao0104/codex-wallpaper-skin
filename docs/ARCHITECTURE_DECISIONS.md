# Architecture Decisions

This log records decisions that affect more than one implementation branch. Add a dated entry before changing a shared boundary.

## ADR-001 — Reversible presentation layer

**Status:** accepted

Codex Wallpaper Skin injects a reversible presentation layer through a verified local CDP endpoint. It does not modify or replace files in the official Codex package and does not read conversation content.

## ADR-002 — Native Wallpaper Engine as the high-fidelity backend

**Status:** accepted direction

Wallpaper Engine remains responsible for rendering complex Scene and controlled Web wallpapers. The companion captures native output and forwards bounded input instead of continuously adding per-wallpaper shader patches. The safe in-browser scene renderer remains a compatibility fallback, not the universal backend.

## ADR-003 — Product convergence from parallel implementations

**Status:** accepted

Parallel Codex- and DeepSeek-assisted branches explore solutions independently from the same specification and baseline. Integration is component-based and product-first. The final implementation may select either candidate, combine compatible strengths, or reject both.

## ADR-004 — No Workshop media in source control

**Status:** accepted

Real Workshop projects may be used for local manual validation but are never copied into the repository, CI artifacts, Issues, or releases without explicit redistribution rights. Synthetic or appropriately licensed scenes are used for automation.

## ADR-005 — Application wallpapers remain unsupported

**Status:** accepted

Application wallpapers execute arbitrary programs and remain outside the product safety boundary. Discovery and apply paths must reject them.

## ADR-006 — Human-controlled releases

**Status:** accepted

Automation may build and test candidates, but every GitHub Release is created as a draft and published only after explicit maintainer approval. Code signing is deferred until a later distribution stage.
