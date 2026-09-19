# Development Workflow

## Purpose

The workflow exists to produce the best maintainable product. Codex, DeepSeek, human contributors, and future tools are implementation sources, not competitors. Decisions are based on product gates, reproducible evidence, and integration cost.

## Branch roles

- `main`: stable integration baseline; no experiments or direct feature commits.
- `codex/issue-<number>-<task>`: one Codex-assisted implementation.
- `deepseek/issue-<number>-<task>`: one DeepSeek-assisted implementation.
- `hotfix/issue-<number>-<task>`: urgent correction from the current `main`.
- `docs/issue-<number>-<task>`: documentation-only work.

Versions are tracked by GitHub milestones and eventually tags/releases. A completed development branch is deleted after its useful commits are integrated or consciously rejected; its commit history remains available.

## Workspace isolation

The authoritative workspace stays on `main`. Codex and DeepSeek use independent clones with independent Git metadata. Each implementation may modify only its assigned clone and branch. Local test assets, Wallpaper Engine projects, captured frames, diagnostics, API keys, and user state must not be copied between clones through Git.

## One task, one shared specification

Parallel work starts from one Issue containing:

1. User-visible problem and reproduction steps.
2. Product goal and explicit non-goals.
3. Supported wallpaper/backend types.
4. Safety and privacy boundaries.
5. Required automated and manual tests.
6. Performance and recovery expectations.
7. Acceptance gates that every implementation must meet.

Branches start from the same `main` commit. If the specification changes, update the Issue first and notify every branch. Do not silently give one implementation easier requirements.

## Integration flow

1. Create or select an Issue and milestone.
2. Create one branch per implementation from the same `main` commit.
3. Implement and test independently.
4. Open draft pull requests early; keep incomplete work marked draft.
5. Fill in the same evidence sections in every pull request.
6. Review by component using `IMPLEMENTATION_CONVERGENCE.md`.
7. Choose one of four outcomes for each component: adopt implementation A, adopt implementation B, combine compatible strengths, or adopt neither and revise the design.
8. Build the converged candidate on an integration branch when hybrid work is needed.
9. Run the full acceptance suite again on the converged candidate.
10. Merge only after the product gates pass and the maintainer approves.

Do not combine code merely to represent both sources. Hybrid integration is justified only when the result is simpler, safer, or measurably better than either candidate alone.

## Conflict and synchronization rules

- Fetch and integrate the latest `main` before final validation.
- Never fix a conflict by replacing another branch wholesale.
- Preserve unrelated user changes and document behavioral migrations.
- Record shared-boundary decisions in `ARCHITECTURE_DECISIONS.md` before branches diverge further.
- No automated process may publish a release. Releases remain drafts until the maintainer approves publication.

## Required evidence

Each pull request reports build/self-test results, supported and unsupported cases, visual fidelity, pointer behavior, CPU/GPU/memory observations, failure recovery, security impact, state compatibility, dependencies, and rollback steps. Real Workshop assets may be tested locally but must not be committed. Automated tests use synthetic or appropriately licensed fixtures.

## Definition of done

A feature is done only when the converged product—not merely an individual branch—passes all hard gates, restores safely, has understandable user state, contains no sensitive or copyrighted test material, and can be maintained without per-wallpaper patches as the normal strategy.
