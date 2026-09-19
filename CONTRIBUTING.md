# Contributing

Contributions are welcome when they preserve the project's narrow boundary: reversible background presentation without modifying the official Codex package or reading user content.

Before submitting a change:

1. Describe the user-visible problem and the Codex/Windows versions used.
2. Keep selectors narrow and add a fixture or self-test for compatibility changes.
3. Run `scripts/build-companion.ps1`, the companion `--self-test`, and `node scripts/runtime-smoke-test.mjs`. If Wallpaper Engine scenes are installed, also run `node scripts/scene-render-smoke-test.mjs`.
4. Verify **Restore Codex background** after apply, switch, navigation, and missing-media cases.
5. Do not add third-party wallpaper files, OpenAI/Valve/Wallpaper Engine logos, telemetry, online Workshop download, or Application-wallpaper execution.
6. Document measurable performance changes with a stock-versus-skin baseline.

Use clear commits and include license attribution for any reused code or assets. By contributing, you agree that your contribution is licensed under Apache-2.0.

Building requires the .NET 8 SDK; the full acceptance flow also needs Node.js. End users do not need Node.js, Python, PyYAML, or .NET when using the self-contained release. The retained Skill-related files are outside the current GUI delivery scope.
