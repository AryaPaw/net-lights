---
name: net-lights-ship
description: >-
  Net Lights (C# / .NET 10 / WinForms tray) local verification, CI, version
  bump, git push, and GitHub Release via tag v*.*.*. Use when running all tests,
  shipping, releasing, tagging, pushing main, fixing CI flakes, or the user
  says прогон, все тесты, релиз, пайплайн, CI, or 1.x.x.
---

# Net Lights ship

This repo already has CI. Do not invent a second pipeline. Local green is required and **not enough** if tests share static process state.

## Always before "tests pass" or a push of app/test code

From the repo root, Release, all three projects. Do not skip soak. Do not stop after `dotnet build`.

```ps1
pwsh -File scripts/verify.ps1
```

Local verification, CI and release use `scripts/verify.ps1`: locked restore, Release build, sequential unit/integration/soak smoke and the coverage gate. Publish remains a separate packaging step. Coverage is collected on unit and integration tests, then merged by source line. Sandbox, visual acceptance and long resource runs are separate checks; the default soak is only a short smoke.

If any fail: fix, re-run the failing project, then re-run all three. Do not push.

## Integration tests vs GitHub runners

xUnit on CI runs test classes **in parallel**. `SettingsStore.RootDirectory` is a **static** path. Tests that assign it and then `Load`/`Save`/`LoadPool` will flake if they run together.

Any test class that writes `SettingsStore.RootDirectory` must be in:

```csharp
[CollectionDefinition("SettingsFiles", DisableParallelization = true)]
public sealed class SettingsFilesCollection;

[Collection("SettingsFiles")]
public sealed class TheTestClass
```

Current members: `GeoCountryTrayTests`, `UiRendererTests`, `LocationHistoryTests`, `StateHistoryAndExportTests`. Add new writers to the same collection. Do not "fix" a flake by asserting less.

Wait for async work (`Letters`, `Visible`), not a side counter that increments before the UI/state updates.

`NotifyIcon` / WinForms tests can still flake on a headless runner. Prefer waiting on observable state with a timeout, not `Task.Delay` only.

## Do not commit

- `TODO.md`
- `docs/images/tray-mock.html` unless README uses it
- Unintended `packages.lock.json` churn from `dotnet publish -r win-x64` (ILLink / `win-x64` graphs). Preserve pre-existing lockfile edits; inspect the diff and remove only task-owned unintended churn. Never blindly restore user changes. Verify the current CI publish settings before relying on them.

## Local exe

Owner runs `artifacts/publish/NetLights.exe`. After tray/UI/updater/version changes: `scripts/run-local.ps1`, then confirm that process path and `ProductVersion` match `Directory.Build.props`.

## Version

Only when a version proposal or release is requested, propose `x.y.Z` with a brief reason if the exact version is missing and wait before editing version metadata. An already specified exact version needs no second confirmation. Don't interrupt ordinary fixes, tests, or pushes with an unsolicited version bump.

Default: patch for tray/copy/CI; minor for user-visible features; major only when explicitly requested. The project root `AGENTS.md` owns this convention.

## Push

Only when the active task explicitly authorizes push to the intended target. Authorization persists for that same task/action/target unless revoked; a status question or new message doesn't reset it. No force-push without specific authorization. Follow the global publication gate.

## Release

Only when the active task explicitly authorizes the release/tag action and target. Pushing `main` alone doesn't authorize a release. Keep version metadata in the final verified and reviewed candidate before tagging.

1. User confirmed the version number; it is in `Directory.Build.props`.
2. All three test projects passed locally.
3. Create a coherent intended commit only if authorized or necessary for the specifically requested publication workflow; preserve unrelated staging. Record its SHA/tree. Inspect commit-hook changes and rerun the affected verification/review before treating it as the final candidate.
4. Push `main` only when that branch push is explicitly authorized or unambiguously included in the requested documented release workflow. Use `ap-deploy` with the recorded verified SHA and valid gate evidence, not an unchecked current branch tip. A local tag request alone doesn't authorize a branch push.
5. Create `vX.Y.Z` at the recorded verified SHA only when tag creation is authorized; verify the tag resolves to that SHA and the version matches metadata. Push that exact tag only when remote tag publication is authorized. Don't replace an existing tag, implicitly use a later `HEAD`, or treat a local tag request as permission to push it.
6. Inspect the current `.github/workflows/release.yml` before sending a tag: this repository's `v*.*.*` workflow builds and publishes a GitHub Release. Require authorization for that publication outcome; don't add `gh release create` unless the user authorizes that separate fallback after a workflow failure.
7. Watch only the CI/release runs triggered by authorized actions. Distinguish main CI, tag CI, and release status. Investigate failures and rerun affected gates; don't push a fix or create another tag without authorization for that action/target in the active task.

Release notes body is already Russian in `release.yml`. User-facing product text stays Russian.

## After a push

`gh run list --limit 8`. Subscribe or watch until `ci` on `main` is green. If integration fails, read `--log-failed` before guessing.
