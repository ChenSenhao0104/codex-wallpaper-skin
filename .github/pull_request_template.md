## Product outcome

Describe the user-visible improvement and link the Issue (`Closes #...`). Do not frame the pull request as a comparison of authors or tools.

## Implementation

- Approach and important component boundaries:
- Files/interfaces changed:
- Dependencies added or updated:
- State/compatibility migration:

## Evidence

- [ ] Debug/Release build succeeds as applicable
- [ ] Companion `--self-test` passes
- [ ] `node scripts/runtime-smoke-test.mjs` passes
- [ ] `scripts/repository-hygiene.ps1` passes
- [ ] Restore tested after apply, switch, navigation, and failure
- [ ] Existing image and video paths checked for regression

Report visual fidelity, pointer interaction, CPU/GPU/memory observations, frame quality/recovery, and the exact synthetic or locally referenced test cases.

## Safety and distribution

- [ ] No API keys, credentials, personal paths, private content, or Workshop media are committed
- [ ] No official Codex files are modified
- [ ] Application wallpapers remain rejected
- [ ] New network/audio behavior is explicitly documented
- [ ] Third-party code/assets have compatible licensing and attribution

## Limitations and rollback

- Known limitations:
- Failure behavior:
- Rollback/restore method:
- Follow-up work:

## Convergence notes

Identify reusable strengths, incompatibilities, and components that could improve the final integrated product. Acceptable conclusions include adopting this implementation, another implementation, a deliberate hybrid, or neither.
