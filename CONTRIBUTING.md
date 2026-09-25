# Contributing

Contributions are welcome when they preserve the project's narrow boundary: reversible background presentation without modifying the official Codex package or reading user content.

## Product-first workflow

`main` is the stable integration branch. Work happens on a short-lived branch linked to one GitHub Issue and enters `main` through a pull request. Parallel implementations are complementary experiments, not a contest between their authors or tools. Review concrete components and evidence, then adopt one implementation, combine compatible strengths, or reject both when neither meets the product gates.

Use these branch patterns:

- `codex/issue-<number>-<task>`
- `deepseek/issue-<number>-<task>`
- `hotfix/issue-<number>-<task>`
- `docs/issue-<number>-<task>`

Existing version-named experiment branches may finish under their original names. New work should use Issue-based names; versions belong to milestones, tags, and releases.

Before submitting a change:

1. Sync from the latest `main` before implementation and describe the user-visible problem, supported scope, and Codex/Windows versions.
2. Keep selectors and native-window matching narrow. Add a fixture, synthetic scene, or self-test for compatibility changes.
3. Run `scripts/build-companion.ps1`, the companion `--self-test`, `node scripts/runtime-smoke-test.mjs`, and `scripts/repository-hygiene.ps1`. If Wallpaper Engine scenes are installed, also run `node scripts/scene-render-smoke-test.mjs`.
4. Verify **Restore Codex background** after apply, switch, navigation, capture failure, and missing-media cases.
5. Record image fidelity, interaction, resource use, recovery behavior, security impact, known limitations, and rollback steps in the pull request.
6. Do not add third-party wallpaper files, private screenshots, credentials, personal paths, OpenAI/Valve/Wallpaper Engine logos, telemetry, online Workshop downloads, or Application-wallpaper execution.
7. Never commit an API key. Local Workshop projects and diagnostic exports stay outside Git.

For installer releases, also compile `installer/CodexWallpaperSkin.iss` through `scripts/build-installer.ps1`, run `scripts/installer-smoke-test.ps1` after exiting the controller, and verify a clean current-user install, an in-place upgrade, and a data-preserving silent uninstall. When `main` is older than the candidate, also run `scripts/installer-cross-version-smoke-test.ps1` with the stable and candidate installers. It must verify the installed executable and registry versions plus unchanged `state.json` and `library.json` hashes before and after upgrade/uninstall. The installer must never terminate Codex, and generated installers remain under ignored `dist/` output.

`scripts/version-consistency-test.ps1` keeps project, manifest, installer, and documentation versions aligned. `scripts/write-release-manifest.ps1` records artifact hashes and the real Authenticode result. If signing is enabled, use the current-user certificate store and a trusted HTTPS timestamp service through `scripts/build-installer.ps1`; never commit a certificate, token, password, private key, or signing-service credential.

Do not push experimental commits directly to `main`, force-push shared branches, or resolve conflicts by overwriting another implementation. A maintainer decides the final integration after product-gate review. Keep commits focused and include license attribution for reused code or assets.

By contributing, you agree that your contribution is licensed under Apache-2.0. Building requires the .NET 8 SDK; the full acceptance flow also needs Node.js. End users do not need Node.js, Python, PyYAML, or .NET when using the self-contained release. The retained Skill-related files are outside the current GUI delivery scope.

The complete workflow is documented in `docs/DEVELOPMENT_WORKFLOW.md`.
